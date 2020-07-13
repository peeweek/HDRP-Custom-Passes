using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;

class Raymarch2D : CustomPass
{
    public LayerMask cloudLayer = 0;
    public Light sunLight;
    [Min(1.0f)]
    public float ThicknessScale = 150.0f;
    [Range(0.1f, 100.0f)]
    public float ThicknessSoftKnee = 3.5f;

    RTHandle cloudBuffer;
    RTHandle cloudDepthBuffer;
    RTHandle cloudBufferHalfRes;
    RTHandle lightBuffer;

    Material cloudMaterial;
    ShaderTagId[] shaderTags;

    static class ShaderID
    {
        public static readonly int _BlitTexture = Shader.PropertyToID("_BlitTexture");
        public static readonly int _BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
        public static readonly int _BlitMipLevel = Shader.PropertyToID("_BlitMipLevel");
        public static readonly int _Radius = Shader.PropertyToID("_Radius");
        public static readonly int _Source = Shader.PropertyToID("_Source");
        public static readonly int _ColorBufferCopy = Shader.PropertyToID("_ColorBufferCopy");
        public static readonly int _LightBuffer = Shader.PropertyToID("_LightBuffer");
        public static readonly int _MaskDepth = Shader.PropertyToID("_MaskDepth");
        public static readonly int _InvertMask = Shader.PropertyToID("_InvertMask");
        public static readonly int _ViewPortSize = Shader.PropertyToID("_ViewPortSize");
        public static readonly int _ThicknessScale = Shader.PropertyToID("_ThicknessScale");
        public static readonly int _ThicknessSoftKnee = Shader.PropertyToID("_ThicknessSoftKnee");
    }

    // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
    // When empty this render pass will render to the active camera render target.
    // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
    // The render pipeline will ensure target setup and clearing happens in an performance manner.
    protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
    {
        cloudMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/FullScreen/CloudsRaymarch2D"));

        // Allocate the buffers used for the blur in half resolution to save some memory
        cloudBuffer = RTHandles.Alloc(
            Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
            colorFormat: GraphicsFormat.R16_SFloat, // We only need a 1 channel mask to composite the blur and color buffer copy
            useDynamicScale: true, name: "Cloud Mask"
        );

        cloudDepthBuffer = RTHandles.Alloc(
            Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
            colorFormat: GraphicsFormat.R16_UInt, useDynamicScale: true,
            name: "Cloud Depth", depthBufferBits: DepthBits.Depth16
        );

        cloudBufferHalfRes = RTHandles.Alloc(
            Vector2.one * 0.5f, TextureXR.slices, dimension: TextureXR.dimension,
            colorFormat: GraphicsFormat.R16_SFloat, // We only need a 1 channel mask to composite the blur and color buffer copy
            useDynamicScale: true, name: "Cloud Mask (¼res)"
        );

        lightBuffer = RTHandles.Alloc(
            Vector2.one * 0.5f, TextureXR.slices, dimension: TextureXR.dimension,
            colorFormat: GraphicsFormat.R16G16B16A16_SFloat, 
            useDynamicScale: true, name: "Light Buffer (¼res)"
        );

        shaderTags = new ShaderTagId[4]
        {
            new ShaderTagId("Forward"),
            new ShaderTagId("ForwardOnly"),
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("FirstPass"),
        };
    }

    protected override void Execute(ScriptableRenderContext renderContext, CommandBuffer cmd, HDCamera hdCamera, CullingResults cullingResult)
    {
        // Draw all clouds in a separate buffer.
        if (cloudMaterial != null)
        {
            DrawCloudObjects(renderContext, cmd, hdCamera, cullingResult);
        }

        // Resolve all clouds on screen
        if(sunLight != null)
        {
            ResolveClouds(cmd, hdCamera);
        }
    }

    protected override void AggregateCullingParameters(ref ScriptableCullingParameters cullingParameters, HDCamera hdCamera)
        => cullingParameters.cullingMask |= (uint)cloudLayer.value;

    void DrawCloudObjects(ScriptableRenderContext renderContext, CommandBuffer cmd, HDCamera hdCamera, CullingResults cullingResult)
    {
        // Render the objects in the layer blur mask into a mask buffer with their materials so we keep the alpha-clip and transparency if there is any.
        var result = new RendererListDesc(shaderTags, cullingResult, hdCamera.camera)
        {
            rendererConfiguration = PerObjectData.None,
            renderQueueRange = RenderQueueRange.all,
            sortingCriteria = SortingCriteria.BackToFront,
            excludeObjectMotionVectors = false,
            layerMask = cloudLayer//,
            //stateBlock = new RenderStateBlock(RenderStateMask.Depth) { depthState = new DepthState(true, CompareFunction.Always) },
        };

        CoreUtils.SetRenderTarget(cmd, cloudBuffer, cloudDepthBuffer, ClearFlag.All);
        HDUtils.DrawRendererList(renderContext, cmd, RendererList.Create(result));
    }

    // We need the viewport size in our shader because we're using half resolution render targets (and so the _ScreenSize
    // variable in the shader does not match the viewport).
    void SetViewPortSize(CommandBuffer cmd, MaterialPropertyBlock block, RTHandle target)
    {
        Vector2Int scaledViewportSize = target.GetScaledSize(target.rtHandleProperties.currentViewportSize);
        block.SetVector(ShaderID._ViewPortSize, new Vector4(scaledViewportSize.x, scaledViewportSize.y, 1.0f / scaledViewportSize.x, 1.0f / scaledViewportSize.y));
    }

    void ResolveClouds(CommandBuffer cmd, HDCamera hdCam)
    {
        RTHandle source;
        // Retrieve the target buffer of the blur from the UI:
        if (targetColorBuffer == TargetBuffer.Camera)
            GetCameraBuffers(out source, out _);
        else
            GetCustomBuffers(out source, out _);


        

        using (new ProfilingScope(cmd, new ProfilingSampler("Resolve Clouds")))
        {
            var compositingProperties = new MaterialPropertyBlock();
            compositingProperties.SetTexture(ShaderID._Source, cloudBuffer);
            compositingProperties.SetTexture(ShaderID._LightBuffer, lightBuffer);
            compositingProperties.SetFloat(ShaderID._ThicknessScale, ThicknessScale);
            compositingProperties.SetFloat(ShaderID._ThicknessSoftKnee, ThicknessSoftKnee);
            SetViewPortSize(cmd, compositingProperties, source);
            HDUtils.DrawFullScreen(cmd, cloudMaterial, source, compositingProperties, shaderPassId: 0);
        }
    }

    // release all resources
    protected override void Cleanup()
    {
        CoreUtils.Destroy(cloudMaterial);
        cloudBuffer.Release();
        cloudDepthBuffer.Release();
        cloudBufferHalfRes.Release();
        lightBuffer.Release();
    }
}