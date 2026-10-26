using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using ClientFileDescriptor = FFXIVClientStructs.FFXIV.Client.System.File.FileDescriptor;

namespace Intoner.Objects.Resources
{
    /// <summary> shared readers for unpacked object resources </summary>
    internal sealed unsafe class ObjectResourceReadBindings
    {
        /// <summary> reads an unpacked texture at the requested lod </summary>
        /// <param name="resetPosition">whether the reader resets the descriptor's file position</param>
        internal delegate byte LoadTextureDelegate(TextureResourceHandle* handle, int lod, ClientFileDescriptor* descriptor, [MarshalAs(UnmanagedType.I1)] bool resetPosition);

        /// <summary> reads an unpacked sound </summary>
        /// <param name="resetPosition">non zero when the reader resets the descriptor's file position</param>
        internal delegate byte LoadSoundDelegate(ResourceHandle* handle, ClientFileDescriptor* descriptor, byte resetPosition);

        /// <summary> updates the texture category </summary>
        internal delegate void UpdateTextureCategoryDelegate(TextureResourceHandle* handle);

        /// <summary> unpacked model reader, or null when unavailable </summary>
        public readonly ResourceHandle.Delegates.Read? LoadModel;

        /// <summary> unpacked texture reader, or null when unavailable </summary>
        public readonly LoadTextureDelegate? LoadTexture;

        /// <summary> unpacked sound reader, or null when unavailable </summary>
        public readonly LoadSoundDelegate? LoadSound;

        /// <summary> texture category update, or null when unavailable </summary>
        public readonly UpdateTextureCategoryDelegate? UpdateTextureCategory;

        public ObjectResourceReadBindings(ILogger<ObjectResourceReadBindings> logger, ISigScanner sigScanner)
        {
            LoadModel = InteropHookUtility.CreateDelegate<ResourceHandle.Delegates.Read>(
                logger,
                sigScanner,
                IntonerSignatures.ResourceLoadMdlFileLocal);
            LoadTexture = InteropHookUtility.CreateDelegate<LoadTextureDelegate>(
                logger,
                sigScanner,
                IntonerSignatures.ResourceLoadTexFileLocal);
            LoadSound = InteropHookUtility.CreateDelegate<LoadSoundDelegate>(
                logger,
                sigScanner,
                IntonerSignatures.ResourceLoadScdFileLocal);
            UpdateTextureCategory = InteropHookUtility.CreateDelegate<UpdateTextureCategoryDelegate>(
                logger,
                sigScanner,
                IntonerSignatures.ResourceUpdateTextureCategory);
        }
    }
}
