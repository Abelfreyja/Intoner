using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace Intoner.Objects.Interop
{
    /// <summary> tracks placed collider ownership across creation and furniture callbacks </summary>
    internal sealed unsafe class ObjectCollisionOwnership : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly HashSet<ObjectCollisionOwner> _owners = [];
        private readonly Dictionary<nint, FurnitureNativeInstance> _instances = [];
        private readonly Dictionary<nint, ObjectCollisionOwner> _colliders = [];
        private readonly HashSet<nint> _creators = [];
        private readonly ThreadLocal<ObjectCollisionOwner?> _current = new();
        private int _cameraColliders;

        public ObjectCollisionOwner? CurrentOwner => _current.Value;
        public bool HasCameraColliders => Volatile.Read(ref _cameraColliders) > 0;

        public void RegisterOwner(ObjectCollisionOwner owner)
        {
            lock (_lock)
            {
                _owners.Add(owner);
            }
        }

        public bool ContainsOwner(ObjectCollisionOwner owner)
        {
            lock (_lock)
            {
                return _owners.Contains(owner);
            }
        }

        public bool RemoveOwner(ObjectCollisionOwner owner)
        {
            lock (_lock)
            {
                return _owners.Remove(owner);
            }
        }

        public ObjectCollisionOwner[] GetOwners()
        {
            lock (_lock)
            {
                return _owners.ToArray();
            }
        }

        public void SetCreators(ReadOnlySpan<nint> primaryCreators, nint secondaryCreator)
        {
            foreach (nint creator in primaryCreators)
            {
                _creators.Add(creator);
            }

            _creators.Add(secondaryCreator);
        }

        public Scope EnterCreation(ObjectCollisionOwner root)
            => new(this, root.IsIsolated ? root : null);

        public Scope EnterInstance(ILayoutInstance* instance)
        {
            lock (_lock)
            {
                return new Scope(this, _instances.GetValueOrDefault((nint)instance));
            }
        }

        public void Register(ILayoutInstance* instance)
        {
            FurnitureNativeInstance? root = _current.Value as FurnitureNativeInstance;
            bool owned = root is not null && (instance->Id.Type is InstanceType.SharedGroup or InstanceType.HelperObject
                or InstanceType.Light or InstanceType.Vfx or InstanceType.EnvSpace or InstanceType.EnvLocation
                || _creators.Contains((nint)instance->VirtualTable->CreatePrimary)
                || _creators.Contains((nint)instance->VirtualTable->CreateSecondary));
            lock (_lock)
            {
                _instances.Remove((nint)instance);

                if (owned)
                {
                    _instances.Add((nint)instance, root!);
                }
            }
        }

        public bool Contains(ILayoutInstance* instance, FurnitureNativeInstance root)
        {
            lock (_lock)
            {
                return _instances.GetValueOrDefault((nint)instance) == root;
            }
        }

        public bool IsHidden(ILayoutInstance* instance)
        {
            lock (_lock)
            {
                return _instances.GetValueOrDefault((nint)instance) is { Visible: false };
            }
        }

        public void Remove(ILayoutInstance* instance)
        {
            lock (_lock)
            {
                _instances.Remove((nint)instance);
            }
        }

        public void RegisterCollider(Collider* collider, ulong layerMask)
        {
            ObjectCollisionOwner? root = _current.Value;
            if (collider == null || root is null)
            {
                return;
            }

            lock (_lock)
            {
                nint address = (nint)collider;
                RemoveColliderCore(address);
                _colliders.Add(address, root);
                (root.ColliderLayers ??= []).Add(address, layerMask);

                if (Participates(root, layerMask))
                {
                    ++_cameraColliders;
                }

                collider->LayerMask = root.Visible && root.CameraCollision ? layerMask : 0;
            }
        }

        public void RemoveCollider(Collider* collider)
        {
            lock (_lock)
            {
                RemoveColliderCore((nint)collider);
            }
        }

        public void SetParticipation(ObjectCollisionOwner root, bool visible, bool cameraCollision)
        {
            lock (_lock)
            {
                bool wasEnabled = root.Visible && root.CameraCollision;
                bool enabled = visible && cameraCollision;
                root.Visible = visible;
                root.CameraCollision = cameraCollision;

                if (root.ColliderLayers is not { } layers)
                {
                    return;
                }

                foreach ((nint address, ulong layerMask) in layers)
                {
                    if (wasEnabled != enabled && (layerMask & ObjectCollisionScene.CameraLayer) != 0)
                    {
                        _cameraColliders += enabled ? 1 : -1;
                    }

                    ((Collider*)address)->LayerMask = enabled ? layerMask : 0;
                }
            }
        }

        private void RemoveColliderCore(nint address)
        {
            if (_colliders.Remove(address, out ObjectCollisionOwner? root))
            {
                root.ColliderLayers!.Remove(address, out ulong layerMask);

                if (Participates(root, layerMask))
                {
                    --_cameraColliders;
                }
            }
        }

        private static bool Participates(ObjectCollisionOwner root, ulong layerMask)
            => root.Visible && root.CameraCollision && (layerMask & ObjectCollisionScene.CameraLayer) != 0;

        public void Dispose()
        {
            _current.Dispose();
            _owners.Clear();
            _instances.Clear();
            _colliders.Clear();
            _creators.Clear();
            _cameraColliders = 0;
        }

        public readonly struct Scope : IDisposable
        {
            private readonly ObjectCollisionOwnership? _owner;
            private readonly ObjectCollisionOwner? _previous;

            internal Scope(ObjectCollisionOwnership owner, ObjectCollisionOwner? root)
            {
                _owner = owner;
                _previous = owner._current.Value;
                owner._current.Value = root;
            }

            public void Dispose()
            {
                if (_owner is not null)
                {
                    _owner._current.Value = _previous;
                }
            }
        }
    }
}
