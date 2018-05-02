using System;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
[GenerateHLSL]
public struct DensityVolumeData
{
    public Vector3 scattering; // [0, 1], prefer sRGB
    public float   extinction; // [0, 1], prefer sRGB

    public static DensityVolumeData GetNeutralValues()
    {
        DensityVolumeData data;

        data.scattering = Vector3.zero;
        data.extinction = 0;

        return data;
    }
} // struct VolumeProperties

public class VolumeRenderingUtils
{
    public static float MeanFreePathFromExtinction(float extinction)
    {
        return 1.0f / extinction;
    }

    public static float ExtinctionFromMeanFreePath(float meanFreePath)
    {
        return 1.0f / meanFreePath;
    }

    public static Vector3 AbsorptionFromExtinctionAndScattering(float extinction, Vector3 scattering)
    {
        return new Vector3(extinction, extinction, extinction) - scattering;
    }

    public static Vector3 ScatteringFromExtinctionAndAlbedo(float extinction, Vector3 albedo)
    {
        return extinction * albedo;
    }

    public static Vector3 AlbedoFromMeanFreePathAndScattering(float meanFreePath, Vector3 scattering)
    {
        return meanFreePath * scattering;
    }
}

[Serializable]
public struct DensityVolumeParameters
{
    public Color albedo;       // Single scattering albedo: [0, 1]. Alpha is ignored
    public float meanFreePath; // In meters: [1, 1000000]. Should be chromatic - this is an optimization!
    public float anisotropy;   // Controls the phase function: [-1, 1]

    public void Constrain()
    {
        albedo.r = Mathf.Clamp01(albedo.r);
        albedo.g = Mathf.Clamp01(albedo.g);
        albedo.b = Mathf.Clamp01(albedo.b);
        albedo.a = 1.0f;

        meanFreePath = Mathf.Clamp(meanFreePath, 1.0f, float.MaxValue);

        anisotropy = Mathf.Clamp(anisotropy, -1.0f, 1.0f);
    }

    public DensityVolumeData GetData()
    {
        DensityVolumeData data = new DensityVolumeData();

        data.extinction = VolumeRenderingUtils.ExtinctionFromMeanFreePath(meanFreePath);
        data.scattering = VolumeRenderingUtils.ScatteringFromExtinctionAndAlbedo(data.extinction, (Vector3)(Vector4)albedo);

        return data;
    }
} // class VolumeParameters

public struct DensityVolumeList
{
    public List<OrientedBBox>      bounds;
    public List<DensityVolumeData> density;
}

public class VolumetricLightingSystem
{
    public enum VolumetricLightingPreset
    {
        Off,
        Normal,
        Ultra,
        Count
    } // enum VolumetricLightingPreset

    [Serializable]
    public struct ControllerParameters
    {
        public float vBufferNearPlane;                 // Distance in meters
        public float vBufferFarPlane;                  // Distance in meters
        public float depthSliceDistributionUniformity; // Controls the exponential depth distribution: [0, 1]

        public static ControllerParameters GetDefaults()
        {
            ControllerParameters parameters;

            parameters.vBufferNearPlane                 = 0.5f;
            parameters.vBufferFarPlane                  = 64.0f;
            parameters.depthSliceDistributionUniformity = 0.75f;

            return parameters;
        }
    } // struct ControllerParameters

    public struct VBufferParameters
    {
        public Vector4 resolution;
        public Vector2 sliceCount;
        public Vector4 depthEncodingParams;
        public Vector4 depthDecodingParams;

        public VBufferParameters(int w, int h, int d, ControllerParameters controlParams)
        {
            resolution          = new Vector4(w, h, 1.0f / w, 1.0f / h);
            sliceCount          = new Vector2(d, 1.0f / d);
            depthEncodingParams = Vector4.zero; // C# doesn't allow function calls before all members have been init
            depthDecodingParams = Vector4.zero; // C# doesn't allow function calls before all members have been init

            Update(controlParams);
        }

        public void Update(ControllerParameters controlParams)
        {
            float n = controlParams.vBufferNearPlane;
            float f = controlParams.vBufferFarPlane;
            float c = 2 - 2 * controlParams.depthSliceDistributionUniformity; // remap [0, 1] -> [2, 0]

            depthEncodingParams = ComputeLogarithmicDepthEncodingParams(n, f, c);
            depthDecodingParams = ComputeLogarithmicDepthDecodingParams(n, f, c);
        }

    } // struct Parameters

    public VolumetricLightingPreset preset { get { return (VolumetricLightingPreset)Math.Min(ShaderConfig.s_VolumetricLightingPreset, (int)VolumetricLightingPreset.Count); } }

    static ComputeShader    m_VolumeVoxelizationCS      = null;
    static ComputeShader    m_VolumetricLightingCS      = null;

    List<OrientedBBox>      m_VisibleVolumeBounds       = null;
    List<DensityVolumeData> m_VisibleVolumeData         = null;
    public const int        k_MaxVisibleVolumeCount     = 512;

    // Static keyword is required here else we get a "DestroyBuffer can only be called from the main thread"
    static ComputeBuffer    s_VisibleVolumeBoundsBuffer = null;
    static ComputeBuffer    s_VisibleVolumeDataBuffer   = null;

    // These two buffers do not depend on the frameID and are therefore shared by all views.
    RTHandleSystem.RTHandle m_DensityBufferHandle;
    RTHandleSystem.RTHandle m_LightingBufferHandle;

    public void Build(HDRenderPipelineAsset asset)
    {
        if (preset == VolumetricLightingPreset.Off) return;

        m_VolumeVoxelizationCS = asset.renderPipelineResources.volumeVoxelizationCS;
        m_VolumetricLightingCS = asset.renderPipelineResources.volumetricLightingCS;

        CreateBuffers();
    }

    // RTHandleSystem API expects a function which computes the resolution. We define it here.
    Vector2Int ComputeVBufferSizeXY(Vector2Int screenSize)
    {
        int t = ComputeVBufferTileSize(preset);

        // Ceil(ScreenSize / TileSize).
        int w = (screenSize.x + (t - 1)) / t;
        int h = (screenSize.y + (t - 1)) / t;

        return new Vector2Int(w, h);
    }

    // BufferedRTHandleSystem API expects an allocator function. We define it here.
    RTHandleSystem.RTHandle HistoryBufferAllocatorFunction(string viewName, int frameIndex, RTHandleSystem rtHandleSystem)
    {
        frameIndex &= 1;

        int d = ComputeVBufferSliceCount(preset);

        return rtHandleSystem.Alloc(scaleFunc:         ComputeVBufferSizeXY,
                                    slices:            d,
                                    dimension:         TextureDimension.Tex3D,
                                    colorFormat:       RenderTextureFormat.ARGBHalf,
                                    sRGB:              false,
                                    enableRandomWrite: true,
                                    enableMSAA:        false,
                                    /* useDynamicScale: true, // <- TODO */
                                    name: string.Format("{0}_VBufferHistory{1}", viewName, frameIndex)
        );
    }

    void CreateBuffers()
    {
        Debug.Assert(m_VolumetricLightingCS != null);

        m_VisibleVolumeBounds       = new List<OrientedBBox>();
        m_VisibleVolumeData         = new List<DensityVolumeData>();
        s_VisibleVolumeBoundsBuffer = new ComputeBuffer(k_MaxVisibleVolumeCount, Marshal.SizeOf(typeof(OrientedBBox)));
        s_VisibleVolumeDataBuffer   = new ComputeBuffer(k_MaxVisibleVolumeCount, Marshal.SizeOf(typeof(DensityVolumeData)));

        int d = ComputeVBufferSliceCount(preset);

        m_DensityBufferHandle = RTHandles.Alloc(scaleFunc:         ComputeVBufferSizeXY,
                                                slices:            d,
                                                dimension:         TextureDimension.Tex3D,
                                                colorFormat:       RenderTextureFormat.ARGBHalf,
                                                sRGB:              false,
                                                enableRandomWrite: true,
                                                enableMSAA:        false,
                                                /* useDynamicScale: true, // <- TODO */
                                                name:              "VBufferDensity");

        m_LightingBufferHandle = RTHandles.Alloc(scaleFunc:         ComputeVBufferSizeXY,
                                                 slices:            d,
                                                 dimension:         TextureDimension.Tex3D,
                                                 colorFormat:       RenderTextureFormat.ARGBHalf,
                                                 sRGB:              false,
                                                 enableRandomWrite: true,
                                                 enableMSAA:        false,
                                                 /* useDynamicScale: true, // <- TODO */
                                                 name:              "VBufferIntegral");
    }

    VBufferParameters ComputeVBufferParameters(HDCamera camera)
    {
        ControllerParameters controlParams;

        var controller = camera.camera.GetComponent<VolumetricLightingController>();

        if (controller != null)
        {
            controlParams = controller.parameters;
        }
        else
        {
            controlParams = ControllerParameters.GetDefaults();
        }

        int w = 0, h = 0, d = 0;
        ComputeVBufferResolutionAndScale(preset, camera.camera.pixelWidth, camera.camera.pixelHeight, ref w, ref h, ref d);

        // Start with the same parameters for both frames. Then update them one by one every frame.
        return new VBufferParameters(w, h, d, controlParams);
    }

    public void InitializePerCameraData(HDCamera camera)
    {
        if (preset == VolumetricLightingPreset.Off) return;

        // Start with the same parameters for both frames. Then update them one by one every frame.
        var parameters          = ComputeVBufferParameters(camera);
        camera.vBufferParams    = new VBufferParameters[2];
        camera.vBufferParams[0] = parameters;
        camera.vBufferParams[1] = parameters;

        if (camera.camera.cameraType == CameraType.Game ||
            camera.camera.cameraType == CameraType.SceneView)
        {
            // We don't need reprojection for other view types, such as reflection and preview.
            camera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.VolumetricLighting, HistoryBufferAllocatorFunction);
        }
    }

    // This function relies on being called once per camera per frame.
    // The results are undefined otherwise.
    public void UpdatePerCameraData(HDCamera camera)
    {
        if (preset == VolumetricLightingPreset.Off) return;

        var parameters = ComputeVBufferParameters(camera);

        // Double-buffer. I assume the cost of copying is negligible (don't want to use the frame index).
        camera.vBufferParams[1] = camera.vBufferParams[0];
        camera.vBufferParams[0] = parameters;

        // Note: resizing of history buffer is automatic (handled by the BufferedRTHandleSystem).
    }

    void DestroyBuffers()
    {
        RTHandles.Release(m_DensityBufferHandle);
        RTHandles.Release(m_LightingBufferHandle);

        CoreUtils.SafeRelease(s_VisibleVolumeBoundsBuffer);
        CoreUtils.SafeRelease(s_VisibleVolumeDataBuffer);

        m_VisibleVolumeBounds = null;
        m_VisibleVolumeData   = null;
    }

    public void Cleanup()
    {
        if (preset == VolumetricLightingPreset.Off) return;

        DestroyBuffers();

        m_VolumeVoxelizationCS = null;
        m_VolumetricLightingCS = null;
    }

    static int ComputeVBufferTileSize(VolumetricLightingPreset preset)
    {
        switch (preset)
        {
            case VolumetricLightingPreset.Normal:
                return 8;
            case VolumetricLightingPreset.Ultra:
                return 4;
            case VolumetricLightingPreset.Off:
                return 0;
            default:
                Debug.Assert(false, "Encountered an unexpected VolumetricLightingPreset.");
                return 0;
        }
    }

    static int ComputeVBufferSliceCount(VolumetricLightingPreset preset)
    {
        switch (preset)
        {
            case VolumetricLightingPreset.Normal:
                return 64;
            case VolumetricLightingPreset.Ultra:
                return 128;
            case VolumetricLightingPreset.Off:
                return 0;
            default:
                Debug.Assert(false, "Encountered an unexpected VolumetricLightingPreset.");
                return 0;
        }
    }

    // Since a single voxel corresponds to a tile (e.g. 8x8) of pixels,
    // the VBuffer can potentially extend past the boundaries of the viewport.
    // The function returns the fraction of the {width, height} of the VBuffer visible on screen.
    // Note: for performance reasons, the scale is unused (implicitly 1). The error is typically under 1%.
    static Vector2 ComputeVBufferResolutionAndScale(VolumetricLightingPreset preset,
                                                    int screenWidth, int screenHeight,
                                                    ref int w, ref int h, ref int d)
    {
        int t = ComputeVBufferTileSize(preset);

        // Ceil(ScreenSize / TileSize).
        w = (screenWidth  + (t - 1)) / t;
        h = (screenHeight + (t - 1)) / t;
        d = ComputeVBufferSliceCount(preset);

        return new Vector2((float)screenWidth / (float)(w * t), (float)screenHeight / (float)(h * t));
    }

    // See EncodeLogarithmicDepthGeneralized().
    static Vector4 ComputeLogarithmicDepthEncodingParams(float nearPlane, float farPlane, float c)
    {
        Vector4 depthParams = new Vector4();

        float n = nearPlane;
        float f = farPlane;

        c = Mathf.Max(c, 0.001f); // Avoid NaNs

        depthParams.y = 1.0f / Mathf.Log(c * (f - n) + 1, 2);
        depthParams.x = Mathf.Log(c, 2) * depthParams.y;
        depthParams.z = n - 1.0f / c; // Same
        depthParams.w = 0.0f;

        return depthParams;
    }

    // See DecodeLogarithmicDepthGeneralized().
    static Vector4 ComputeLogarithmicDepthDecodingParams(float nearPlane, float farPlane, float c)
    {
        Vector4 depthParams = new Vector4();

        float n = nearPlane;
        float f = farPlane;

        c = Mathf.Max(c, 0.001f); // Avoid NaNs

        depthParams.x = 1.0f / c;
        depthParams.y = Mathf.Log(c * (f - n) + 1, 2);
        depthParams.z = n - 1.0f / c; // Same
        depthParams.w = 0.0f;

        return depthParams;
    }

    void SetPreconvolvedAmbientLightProbe(CommandBuffer cmd, float anisotropy)
    {
        SphericalHarmonicsL2 probeSH = SphericalHarmonicMath.UndoCosineRescaling(RenderSettings.ambientProbe);
        ZonalHarmonicsL2     phaseZH = ZonalHarmonicsL2.GetCornetteShanksPhaseFunction(anisotropy);
        SphericalHarmonicsL2 finalSH = SphericalHarmonicMath.PremultiplyCoefficients(SphericalHarmonicMath.Convolve(probeSH, phaseZH));

        cmd.SetGlobalVectorArray(HDShaderIDs._AmbientProbeCoeffs, SphericalHarmonicMath.PackCoefficients(finalSH));
    }

    float CornetteShanksPhasePartConstant(float anisotropy)
    {
        float g = anisotropy;

        return (1.0f / (4.0f * Mathf.PI)) * 1.5f * (1.0f - g * g) / (2.0f + g * g);
    }

    public void PushGlobalParams(HDCamera camera, CommandBuffer cmd, uint frameIndex)
    {
        if (preset == VolumetricLightingPreset.Off) return;

        var visualEnvironment = VolumeManager.instance.stack.GetComponent<VisualEnvironment>();

        // VisualEnvironment sets global fog parameters: _GlobalAnisotropy, _GlobalScattering, _GlobalExtinction.

        if (visualEnvironment.fogType != FogType.Volumetric)
        {
            // Set the neutral black texture.
            cmd.SetGlobalTexture(HDShaderIDs._VBufferLighting, CoreUtils.blackVolumeTexture);
            return;
        }

        // Get the interpolated anisotropy value.
        var fog = VolumeManager.instance.stack.GetComponent<VolumetricFog>();

        SetPreconvolvedAmbientLightProbe(cmd, fog.anisotropy);

        var currFrameParams = camera.vBufferParams[0];
        var prevFrameParams = camera.vBufferParams[1];

        cmd.SetGlobalVector( HDShaderIDs._VBufferResolution,              currFrameParams.resolution);
        cmd.SetGlobalVector( HDShaderIDs._VBufferSliceCount,              currFrameParams.sliceCount);
        cmd.SetGlobalVector( HDShaderIDs._VBufferDepthEncodingParams,     currFrameParams.depthEncodingParams);
        cmd.SetGlobalVector( HDShaderIDs._VBufferDepthDecodingParams,     currFrameParams.depthDecodingParams);
        cmd.SetGlobalVector( HDShaderIDs._VBufferPrevResolution,          prevFrameParams.resolution);
        cmd.SetGlobalVector( HDShaderIDs._VBufferPrevSliceCount,          prevFrameParams.sliceCount);
        cmd.SetGlobalVector( HDShaderIDs._VBufferPrevDepthEncodingParams, prevFrameParams.depthEncodingParams);
        cmd.SetGlobalVector( HDShaderIDs._VBufferPrevDepthDecodingParams, prevFrameParams.depthDecodingParams);
        cmd.SetGlobalTexture(HDShaderIDs._VBufferLighting,                m_LightingBufferHandle);
    }

    public DensityVolumeList PrepareVisibleDensityVolumeList(HDCamera camera, CommandBuffer cmd)
    {
        DensityVolumeList densityVolumes = new DensityVolumeList();

        if (preset == VolumetricLightingPreset.Off) return densityVolumes;

        var visualEnvironment = VolumeManager.instance.stack.GetComponent<VisualEnvironment>();
        if (visualEnvironment.fogType != FogType.Volumetric) return densityVolumes;

        using (new ProfilingSample(cmd, "Prepare Visible Density Volume List"))
        {
            Vector3 camPosition = camera.camera.transform.position;
            Vector3 camOffset   = Vector3.zero; // World-origin-relative

            if (ShaderConfig.s_CameraRelativeRendering != 0)
            {
                camOffset = camPosition; // Camera-relative
            }

            m_VisibleVolumeBounds.Clear();
            m_VisibleVolumeData.Clear();

            // Collect all visible finite volume data, and upload it to the GPU.
            HomogeneousDensityVolume[] volumes = DensityVolumeManager.manager.GetAllVolumes();

            for (int i = 0; i < Math.Min(volumes.Length, k_MaxVisibleVolumeCount); i++)
            {
                HomogeneousDensityVolume volume = volumes[i];

                // TODO: cache these?
                var obb = OrientedBBox.Create(volume.transform);

                // Handle camera-relative rendering.
                obb.center -= camOffset;

                // Frustum cull on the CPU for now. TODO: do it on the GPU.
                // TODO: account for custom near and far planes of the V-Buffer's frustum.
                // It's typically much shorter (along the Z axis) than the camera's frustum.
                if (GeometryUtils.Overlap(obb, camera.frustum, 6, 8))
                {
                    // TODO: cache these?
                    var data = volume.parameters.GetData();

                    m_VisibleVolumeBounds.Add(obb);
                    m_VisibleVolumeData.Add(data);
                }
            }

            s_VisibleVolumeBoundsBuffer.SetData(m_VisibleVolumeBounds);
            s_VisibleVolumeDataBuffer.SetData(m_VisibleVolumeData);

            // Fill the struct with pointers in order to share the data with the light loop.
            densityVolumes.bounds  = m_VisibleVolumeBounds;
            densityVolumes.density = m_VisibleVolumeData;

            return densityVolumes;
        }
    }

    public void VolumeVoxelizationPass(DensityVolumeList densityVolumes, HDCamera camera, CommandBuffer cmd, FrameSettings settings, uint frameIndex)
    {
        if (preset == VolumetricLightingPreset.Off) return;

        var visualEnvironment = VolumeManager.instance.stack.GetComponent<VisualEnvironment>();
        if (visualEnvironment.fogType != FogType.Volumetric) return;

        using (new ProfilingSample(cmd, "Volume Voxelization"))
        {
            int numVisibleVolumes = m_VisibleVolumeBounds.Count;

            if (numVisibleVolumes == 0)
            {
                // Clear the render target instead of running the shader.
                // Note: the clear must take the global fog into account!
                // CoreUtils.SetRenderTarget(cmd, vBuffer.GetDensityBuffer(), ClearFlag.Color, CoreUtils.clearColorAllBlack);
                // return;

                // Clearing 3D textures does not seem to work!
                // Use the workaround by running the full shader with 0 density
            }

            bool enableClustered = settings.lightLoopSettings.enableTileAndCluster;

            int kernel = m_VolumeVoxelizationCS.FindKernel(enableClustered ? "VolumeVoxelizationClustered"
                                                                           : "VolumeVoxelizationBruteforce");

            var     frameParams = camera.vBufferParams[0];
            Vector4 resolution  = frameParams.resolution;
            float   vFoV        = camera.camera.fieldOfView * Mathf.Deg2Rad;

            // Compose the matrix which allows us to compute the world space view direction.
            Matrix4x4 transform   = HDUtils.ComputePixelCoordToWorldSpaceViewDirectionMatrix(vFoV, resolution, camera.viewMatrix, false);

            cmd.SetComputeTextureParam(m_VolumeVoxelizationCS, kernel, HDShaderIDs._VBufferDensity, m_DensityBufferHandle);
            cmd.SetComputeBufferParam( m_VolumeVoxelizationCS, kernel, HDShaderIDs._VolumeBounds,   s_VisibleVolumeBoundsBuffer);
            cmd.SetComputeBufferParam( m_VolumeVoxelizationCS, kernel, HDShaderIDs._VolumeData,     s_VisibleVolumeDataBuffer);

            // TODO: set the constant buffer data only once.
            cmd.SetComputeMatrixParam( m_VolumeVoxelizationCS, HDShaderIDs._VBufferCoordToViewDirWS,  transform);
            cmd.SetComputeIntParam(    m_VolumeVoxelizationCS, HDShaderIDs._NumVisibleDensityVolumes, numVisibleVolumes);

            int w = (int)resolution.x;
            int h = (int)resolution.y;

            // The shader defines GROUP_SIZE_1D = 8.
            cmd.DispatchCompute(m_VolumeVoxelizationCS, kernel, (w + 7) / 8, (h + 7) / 8, 1);
        }
    }

    // Ref: https://en.wikipedia.org/wiki/Close-packing_of_equal_spheres
    // The returned {x, y} coordinates (and all spheres) are all within the (-0.5, 0.5)^2 range.
    // The pattern has been rotated by 15 degrees to maximize the resolution along X and Y:
    // https://www.desmos.com/calculator/kcpfvltz7c
    static Vector2[] GetHexagonalClosePackedSpheres7()
    {
        Vector2[] coords = new Vector2[7];

        float r = 0.17054068870105443882f;
        float d = 2 * r;
        float s = r * Mathf.Sqrt(3);

        // Try to keep the weighted average as close to the center (0.5) as possible.
        //  (7)(5)    ( )( )    ( )( )    ( )( )    ( )( )    ( )(o)    ( )(x)    (o)(x)    (x)(x)
        // (2)(1)(3) ( )(o)( ) (o)(x)( ) (x)(x)(o) (x)(x)(x) (x)(x)(x) (x)(x)(x) (x)(x)(x) (x)(x)(x)
        //  (4)(6)    ( )( )    ( )( )    ( )( )    (o)( )    (x)( )    (x)(o)    (x)(x)    (x)(x)
        coords[0] = new Vector2( 0,  0);
        coords[1] = new Vector2(-d,  0);
        coords[2] = new Vector2( d,  0);
        coords[3] = new Vector2(-r, -s);
        coords[4] = new Vector2( r,  s);
        coords[5] = new Vector2( r, -s);
        coords[6] = new Vector2(-r,  s);

        // Rotate the sampling pattern by 15 degrees.
        const float cos15 = 0.96592582628906828675f;
        const float sin15 = 0.25881904510252076235f;

        for (int i = 0; i < 7; i++)
        {
            Vector2 coord = coords[i];

            coords[i].x = coord.x * cos15 - coord.y * sin15;
            coords[i].y = coord.x * sin15 + coord.y * cos15;
        }

        return coords;
    }

    public void VolumetricLightingPass(HDCamera camera, CommandBuffer cmd, FrameSettings settings, uint frameIndex)
    {
        if (preset == VolumetricLightingPreset.Off) return;

        var visualEnvironment = VolumeManager.instance.stack.GetComponent<VisualEnvironment>();
        if (visualEnvironment.fogType != FogType.Volumetric) return;

        using (new ProfilingSample(cmd, "Volumetric Lighting"))
        {
            // Only available in the Play Mode because all the frame counters in the Edit Mode are broken.
            bool enableClustered    = settings.lightLoopSettings.enableTileAndCluster;
            bool enableReprojection = Application.isPlaying && camera.camera.cameraType == CameraType.Game;

            int kernel;

            if (enableReprojection)
            {
                kernel = m_VolumetricLightingCS.FindKernel(enableClustered ? "VolumetricLightingClusteredReproj"
                                                                           : "VolumetricLightingBruteforceReproj");
            }
            else
            {
                kernel = m_VolumetricLightingCS.FindKernel(enableClustered ? "VolumetricLightingClustered"
                                                                           : "VolumetricLightingBruteforce");
            }

            var       frameParams = camera.vBufferParams[0];
            Vector4   resolution  = frameParams.resolution;
            float     vFoV        = camera.camera.fieldOfView * Mathf.Deg2Rad;
            // Compose the matrix which allows us to compute the world space view direction.
            Matrix4x4 transform   = HDUtils.ComputePixelCoordToWorldSpaceViewDirectionMatrix(vFoV, resolution, camera.viewMatrix, false);

            Vector2[] xySeq = GetHexagonalClosePackedSpheres7();

            // This is a sequence of 7 equidistant numbers from 1/14 to 13/14.
            // Each of them is the centroid of the interval of length 2/14.
            // They've been rearranged in a sequence of pairs {small, large}, s.t. (small + large) = 1.
            // That way, the running average position is close to 0.5.
            // | 6 | 2 | 4 | 1 | 5 | 3 | 7 |
            // |   |   |   | o |   |   |   |
            // |   | o |   | x |   |   |   |
            // |   | x |   | x |   | o |   |
            // |   | x | o | x |   | x |   |
            // |   | x | x | x | o | x |   |
            // | o | x | x | x | x | x |   |
            // | x | x | x | x | x | x | o |
            // | x | x | x | x | x | x | x |
            float[] zSeq = {7.0f/14.0f, 3.0f/14.0f, 11.0f/14.0f, 5.0f/14.0f, 9.0f/14.0f, 1.0f/14.0f, 13.0f/14.0f};

            int sampleIndex = (int)frameIndex % 7;

            // TODO: should we somehow reorder offsets in Z based on the offset in XY? S.t. the samples more evenly cover the domain.
            // Currently, we assume that they are completely uncorrelated, but maybe we should correlate them somehow.
            Vector4 offset = new Vector4(xySeq[sampleIndex].x, xySeq[sampleIndex].y, zSeq[sampleIndex], frameIndex);

            // Get the interpolated anisotropy value.
            var fog = VolumeManager.instance.stack.GetComponent<VolumetricFog>();

            // TODO: set 'm_VolumetricLightingPreset'.
            // TODO: set the constant buffer data only once.
            cmd.SetComputeMatrixParam( m_VolumetricLightingCS,         HDShaderIDs._VBufferCoordToViewDirWS, transform);
            cmd.SetComputeVectorParam( m_VolumetricLightingCS,         HDShaderIDs._VBufferSampleOffset,     offset);
            cmd.SetComputeFloatParam(  m_VolumetricLightingCS,         HDShaderIDs._CornetteShanksConstant,  CornetteShanksPhasePartConstant(fog.anisotropy));
            cmd.SetComputeTextureParam(m_VolumetricLightingCS, kernel, HDShaderIDs._VBufferDensity,          m_DensityBufferHandle);  // Read
            cmd.SetComputeTextureParam(m_VolumetricLightingCS, kernel, HDShaderIDs._VBufferLightingIntegral, m_LightingBufferHandle); // Write
            if (enableReprojection)
            {
            cmd.SetComputeTextureParam(m_VolumetricLightingCS, kernel, HDShaderIDs._VBufferLightingHistory,  camera.GetPreviousFrameRT((int)HDCameraFrameHistoryType.VolumetricLighting)); // Read
            cmd.SetComputeTextureParam(m_VolumetricLightingCS, kernel, HDShaderIDs._VBufferLightingFeedback, camera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.VolumetricLighting));  // Write
            }

            int w = (int)resolution.x;
            int h = (int)resolution.y;

            // The shader defines GROUP_SIZE_1D = 8.
            cmd.DispatchCompute(m_VolumetricLightingCS, kernel, (w + 7) / 8, (h + 7) / 8, 1);
        }
    }
} // class VolumetricLightingModule
} // namespace UnityEngine.Experimental.Rendering.HDPipeline
