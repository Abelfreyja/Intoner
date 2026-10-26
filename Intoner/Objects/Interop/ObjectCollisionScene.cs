using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Intoner.Objects.Interop
{
    /// <summary> owns a collision scene outside the game's scene list </summary>
    internal sealed unsafe class ObjectCollisionScene : IDisposable
    {
        public const ulong CameraLayer = 1;

        private readonly CameraCollisionBindings _bindings;
        private readonly BGCollisionModule* _module;
        private SceneManager* _manager;

        public SceneManager* Manager => _manager;
        public bool CanCreateBg => _bindings.CanCreateBg;

        public ObjectCollisionScene(CameraCollisionBindings bindings, BGCollisionModule* module)
        {
            _bindings = bindings;
            _module = module;
            _manager = (SceneManager*)NativeMemory.Alloc((nuint)sizeof(SceneManager));
            bindings.SceneManagerCtor(_manager);
            bindings.SceneManagerInit(_manager, module->ResourceManager, *(int*)((byte*)module->SceneManager + 0x10));
        }

        public SceneWrapper* RouteFactory(SceneWrapper* original)
            => original->Manager == _module->SceneManager ? _manager->FirstScene : original;

        public Collider* CreateBgCollider(BgColliderDefinition definition, SceneTransform transform)
        {
            ResolveBgTransform(definition, transform, out Vector3 position, out Vector3 rotation, out Vector3 scale);
            SceneWrapper* scene = _manager->FirstScene;
            Collider* collider;
            switch (definition.Kind)
            {
                case BgCollisionKind.Mesh:
                    if (!ObjectResourcePathEncoding.TryGetPcbPathByteCount(definition.PcbPath, out int count))
                    {
                        throw new ArgumentException("collision path exceeds the PCB path capacity", nameof(definition));
                    }

                    Span<byte> path = stackalloc byte[ObjectResourcePathEncoding.PcbPathCapacity];
                    Encoding.UTF8.GetBytes(definition.PcbPath, path);
                    path[count] = 0;
                    fixed (byte* pointer = path)
                    {
                        collider = (Collider*)scene->AddColliderMesh(CameraLayer, pointer, *(int*)((byte*)_manager + 0x10) == 1,
                            &position, &rotation, &scale);
                    }

                    break;
                case BgCollisionKind.Box:
                    collider = (Collider*)scene->AddColliderBox(CameraLayer, &position, &rotation, &scale);
                    break;
                case BgCollisionKind.Sphere:
                    scale = new Vector3(scale.X);
                    collider = (Collider*)scene->AddColliderSphere(CameraLayer, &position, &rotation, &scale);
                    break;
                case BgCollisionKind.Cylinder:
                    scale.Z = scale.X;
                    collider = (Collider*)scene->AddColliderMeshCylinder(CameraLayer, &position, &rotation, &scale);
                    break;
                case BgCollisionKind.Plane:
                    collider = (Collider*)scene->AddColliderPlane(CameraLayer, &position, &rotation, &scale);
                    break;
                default:
                    throw new ArgumentException("collider definition has no supported shape", nameof(definition));
            }

            if (collider == null)
            {
                return null;
            }

            if (definition.Shape is { } shape)
            {
                if (definition.Kind == BgCollisionKind.Cylinder)
                {
                    _bindings.SetCylinderMeshMaterial((ColliderMesh*)collider, shape.MaterialId, shape.MaterialMask);
                }

                collider->SetMaterial(shape.MaterialId, shape.MaterialMask);
            }

            collider->SetMaterial(definition.MaterialId, definition.MaterialMask);
            return collider;
        }

        public void UpdateBgCollider(Collider* collider, BgColliderDefinition definition, SceneTransform transform)
        {
            ResolveBgTransform(definition, transform, out Vector3 position, out Vector3 rotation, out Vector3 scale);
            collider->SetTranslation(&position);
            collider->SetRotation(&rotation);

            if (definition.Kind == BgCollisionKind.Cylinder)
            {
                _bindings.SetCylinderMeshBounds((ColliderMesh*)collider, &scale);
            }
            else
            {
                collider->SetScale(&scale);
            }
        }

        public void RemoveCollider(Collider* collider)
            => _manager->FirstScene->RemoveCollider(collider);

        private void ResolveBgTransform(BgColliderDefinition definition, SceneTransform transform,
            out Vector3 position, out Vector3 rotation, out Vector3 scale)
        {
            Transform placed = ObjectLayoutInterop.CreateTransform(transform);
            position = rotation = scale = default;
            fixed (Vector3* p = &position, r = &rotation, s = &scale)
            {
                if (definition.Kind == BgCollisionKind.Mesh)
                {
                    _bindings.TransformGetComponents(p, r, s, &placed);
                }
                else
                {
                    BgAnalyticShape shape = definition.Shape ?? throw new ArgumentException("analytic collider definition has no shape", nameof(definition));
                    FileLayerGroupTransform file = new() { Translation = shape.Translation, Rotation = shape.Rotation, Scale = shape.Scale };
                    AnalyticShapeData analytic = new()
                    {
                        BoundsMin = shape.BoundsMin,
                        BoundsMax = shape.BoundsMax,
                        MaterialId = shape.MaterialId,
                        MaterialMask = shape.MaterialMask,
                    };
                    _bindings.TransformFromFile(&analytic.Transform, &file);
                    analytic.Transform.Type = (int)definition.Kind - 1;

                    if (_bindings.GetAnalyticColliderTransform(null, p, r, s, &placed, &analytic) == 0)
                    {
                        throw new InvalidOperationException("analytic collision transform could not be resolved");
                    }
                }
            }

            if (!NumericsUtility.IsFinite(position) || !NumericsUtility.IsFinite(rotation) || !NumericsUtility.IsFinite(scale))
            {
                throw new InvalidOperationException("collision transform is not finite");
            }
        }

        [SuppressMessage("Major Code Smell", "S1244", Justification = "The camera helper distinguishes an exactly zero segment.")]
        public bool TrySweep(Vector4* output, Vector3* start, Vector3* end, float radius, float maximumDistance, ulong material, out byte hit)
        {
            hit = 0;
            SceneManager* manager = _manager;

            if (manager == null || manager->FirstScene->Scene->NumColliders == 0)
            {
                return false;
            }

            Vector3 direction = *end - *start;
            float length = MathF.Sqrt(((direction.Y * direction.Y) + (direction.X * direction.X)) + (direction.Z * direction.Z));

            if (length == 0)
            {
                *output = new Vector4(*end, 0);
                return true;
            }

            direction *= 1f / length;
            _bindings.Vector3Normalize(&direction);
            float distance = maximumDistance > 0 ? maximumDistance : length;
            Vector4 origin = new(*start, radius);
            RaycastMaterialFilter filter = new() { Mask = material, Value = material };

            SceneWrapper* world = _module->SceneManager->FirstScene;
            RaycastHit worldHit = default;
            RaycastHit placedHit = default;
            bool hitWorld = Raycast(world, &worldHit, &origin, &direction, distance, &filter);
            bool hitPlaced = Raycast(manager->FirstScene, &placedHit, &origin, &direction, distance, &filter);

            if (hitPlaced && (!hitWorld || PreferPlacedHit(&worldHit, &placedHit, &direction)))
            {
                *output = new Vector4(placedHit.Point, 0);
                hit = 1;
                return true;
            }

            *output = new Vector4(worldHit.Point, 0);
            hit = hitWorld ? (byte)1 : (byte)0;
            return true;
        }

        public void Dispose()
        {
            if (_manager == null)
            {
                return;
            }

            using CollisionLock writeLock = CollisionLock.Write(_module);
            SceneManager* manager = _manager;

            if (manager == null)
            {
                return;
            }

            _manager = null;
            manager->Dtor(0);
            NativeMemory.Free(manager);
        }

        private bool PreferPlacedHit(RaycastHit* world, RaycastHit* placed, Vector3* direction)
        {
            if (placed->Distance < 0 || placed->Distance > world->Distance + 0.0001f)
            {
                return false;
            }

            if (world->Distance - placed->Distance >= 0.0002f)
            {
                return true;
            }

            Vector3 worldNormal = default;
            Vector3 placedNormal = default;
            _bindings.RaycastHitGetNormal(world, &worldNormal);
            _bindings.RaycastHitGetNormal(placed, &placedNormal);
            return Vector3.Dot(placedNormal, *direction) < Vector3.Dot(worldNormal, *direction);
        }

        private static bool Raycast(SceneWrapper* scene, RaycastHit* hit, Vector4* origin, Vector3* direction,
            float distance, RaycastMaterialFilter* filter)
        {
            RaycastParams parameters = new()
            {
                Algorithm = 5,
                Origin = origin,
                Direction = direction,
                MaxDistance = &distance,
                MaterialFilter = filter,
            };
            return scene->Raycast(hit, CameraLayer, &parameters);
        }
    }
}
