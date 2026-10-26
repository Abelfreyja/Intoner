namespace Intoner.Objects.Interop
{
    internal abstract class ObjectCollisionOwner : IDisposable
    {
        protected CameraCollisionService Collision { get; }

        public bool IsIsolated { get; }
        internal bool Visible { get; set; }
        internal bool CameraCollision { get; set; }
        internal Dictionary<nint, ulong>? ColliderLayers { get; set; }

        protected ObjectCollisionOwner(CameraCollisionService collision, bool isolated, bool visible, bool cameraCollision)
        {
            Collision = collision;
            IsIsolated = isolated;
            Visible = visible;
            CameraCollision = cameraCollision;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected abstract void Dispose(bool disposing);
    }
}
