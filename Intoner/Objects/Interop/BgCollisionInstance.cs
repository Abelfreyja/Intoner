using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Objects.Models;
using Intoner.Scene;

namespace Intoner.Objects.Interop
{
    /// <summary> owns the current authored collider for one standalone bgobject </summary>
    internal sealed unsafe class BgCollisionInstance : ObjectCollisionOwner
    {
        private nint _address;

        internal BgCollisionInstance(CameraCollisionService collision, bool isolated, bool visible, bool cameraCollision)
            : base(collision, isolated, visible, cameraCollision)
        { }

        internal Collider* Instance
        {
            get => (Collider*)Volatile.Read(ref _address);
            set => Volatile.Write(ref _address, (nint)value);
        }

        internal BgColliderDefinition? Definition { get; set; }
        internal SceneTransform? Transform { get; set; }

        public void Apply(BgColliderDefinition? definition, SceneTransform transform, bool visible, bool cameraCollision)
            => Collision.ApplyBg(this, definition, transform, visible, cameraCollision);

        public void Replace(BgColliderDefinition? definition, SceneTransform transform, bool visible, bool cameraCollision)
            => Collision.ApplyBg(this, definition, transform, visible, cameraCollision, replace: true);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Collision.DestroyBg(this);
            }
        }

        internal Collider* TakeInstance()
            => (Collider*)Interlocked.Exchange(ref _address, 0);
    }
}
