using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;

namespace Intoner.Objects.Interop
{
    /// <summary> owns one native furniture root </summary>
    internal sealed unsafe class FurnitureNativeInstance : ObjectCollisionOwner
    {
        private nint _address;

        internal FurnitureNativeInstance(CameraCollisionService collision, bool visible, bool cameraCollision)
            : base(collision, true, visible, cameraCollision)
        { }

        public SharedGroupLayoutInstance* Instance
        {
            get => (SharedGroupLayoutInstance*)Volatile.Read(ref _address);
            internal set => Volatile.Write(ref _address, (nint)value);
        }

        public void SetParticipation(bool visible, bool cameraCollision)
            => Collision.SetParticipation(this, visible, cameraCollision);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Collision.DestroyRoot(this);
            }
        }

        internal SharedGroupLayoutInstance* TakeInstance()
            => (SharedGroupLayoutInstance*)Interlocked.Exchange(ref _address, 0);
    }
}
