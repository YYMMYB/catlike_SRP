﻿using UnityEngine;
using UnityEngine.Rendering;

public class Shadows
{
    const int maxShadowedDirectionalLightCount = 4, maxCascades = 4;
    const string bufferName = "Shadows";
    static int
        dirShadowAtlasId = Shader.PropertyToID("_DirectionalShadowAtlas"),
        dirShadowMatricesId = Shader.PropertyToID("_DirectionalShadowMatrices"),
        cascadeCountId = Shader.PropertyToID("_CascadeCount"),
        cascadeCullingSpheresId = Shader.PropertyToID("_CascadeCullingSpheres"),
        cascadeDataId = Shader.PropertyToID("_CascadeData"),
        shadowAtlasSizeId = Shader.PropertyToID("_ShadowAtlasSize"),
        shadowDistanceFadeId = Shader.PropertyToID("_ShadowDistanceFade");

    static Matrix4x4[]
        dirShadowMatrices = new Matrix4x4[maxShadowedDirectionalLightCount * maxCascades];
    static Vector4[] cascadeCullingSpheres = new Vector4[maxCascades],
        cascadeData = new Vector4[maxCascades];

    static string[] directionalFilterKeywords = {
        "_DIRECTIONAL_PCF3",
        "_DIRECTIONAL_PCF5",
        "_DIRECTIONAL_PCF7",
    };

    static string[] cascadeBlendKeywords = {
        "_CASCADE_BLEND_SOFT",
        "_CASCADE_BLEND_DITHER"
    };

    CommandBuffer buffer = new CommandBuffer
    {
        name = bufferName
    };

    ScriptableRenderContext context;

    CullingResults cullingResults;

    ShadowSettings settings;

    int ShadowedDirectionalLightCount;

    public void Setup(
        ScriptableRenderContext context, CullingResults cullingResults,
        ShadowSettings settings
    )
    {
        this.context = context;
        this.cullingResults = cullingResults;
        this.settings = settings;

        ShadowedDirectionalLightCount = 0;
    }

    void ExecuteBuffer()
    {
        context.ExecuteCommandBuffer(buffer);
        buffer.Clear();
    }

    struct ShadowedDirectionalLight
    {
        public int visibleLightIndex;
        public float slopeScaleBias;
        public float nearPlaneOffset;
    }

    ShadowedDirectionalLight[] ShadowedDirectionalLights =
        new ShadowedDirectionalLight[maxShadowedDirectionalLightCount];

    public Vector3 ReserveDirectionalShadows(Light light, int visibleLightIndex)
    {
        if (ShadowedDirectionalLightCount < maxShadowedDirectionalLightCount &&
            light.shadows != LightShadows.None && light.shadowStrength > 0f &&
            cullingResults.GetShadowCasterBounds(visibleLightIndex, out Bounds b))
        {
            ShadowedDirectionalLights[ShadowedDirectionalLightCount] =
                new ShadowedDirectionalLight
                {
                    visibleLightIndex = visibleLightIndex,
                    slopeScaleBias = light.shadowBias,
                    nearPlaneOffset = light.shadowNearPlane
                };
            return new Vector3(
                light.shadowStrength,
                settings.directional.cascadeCount * ShadowedDirectionalLightCount++,
                light.shadowNormalBias
            );
        }
        return Vector3.zero;
    }

    public void Render()
    {
        if (ShadowedDirectionalLightCount > 0)
        {
            RenderDirectionalShadows();
        }
        else
        {
            buffer.GetTemporaryRT(
                dirShadowAtlasId, 1, 1,
                32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap
            );
        }
    }

    void RenderDirectionalShadows()
    {
        int atlasSize = (int)settings.directional.atlasSize;
        int tiles = ShadowedDirectionalLightCount * settings.directional.cascadeCount;
        int split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
        int tileSize = atlasSize / split;
        buffer.GetTemporaryRT(
            dirShadowAtlasId, atlasSize, atlasSize,
            32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap
        );
        buffer.SetRenderTarget(
            dirShadowAtlasId,
            RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
        );
        buffer.ClearRenderTarget(true, false, Color.clear);
        buffer.BeginSample(bufferName);
        ExecuteBuffer();

        for (int i = 0; i < ShadowedDirectionalLightCount; i++)
        {
            RenderDirectionalShadows(i, split, tileSize);
        }

        buffer.SetGlobalInt(cascadeCountId, settings.directional.cascadeCount);
        buffer.SetGlobalVectorArray(
            cascadeCullingSpheresId, cascadeCullingSpheres
        );
        buffer.SetGlobalMatrixArray(dirShadowMatricesId, dirShadowMatrices);
        float f = 1f - settings.directional.cascadeFade;
        buffer.SetGlobalVector(
            shadowDistanceFadeId,
            new Vector4(1f / settings.maxDistance, 1f / settings.distanceFade, 1f / (1f - f * f))
        );
        buffer.SetGlobalVector(
            shadowAtlasSizeId, new Vector4(atlasSize, 1f / atlasSize)
        );
        buffer.SetGlobalVectorArray(cascadeDataId, cascadeData);
        SetKeywords(
            directionalFilterKeywords, (int)settings.directional.filter - 1
        );
        SetKeywords(
            cascadeBlendKeywords, (int)settings.directional.cascadeBlend - 1
        );
        buffer.EndSample(bufferName);
        ExecuteBuffer();
    }

    void RenderDirectionalShadows(int index, int split, int tileSize)
    {
        ShadowedDirectionalLight light = ShadowedDirectionalLights[index];
        var shadowSettings =
            new ShadowDrawingSettings(cullingResults, light.visibleLightIndex);
        int cascadeCount = settings.directional.cascadeCount;
        int tileOffset = index * cascadeCount;
        Vector3 ratios = settings.directional.CascadeRatios;
        for (int i = 0; i < cascadeCount; i++)
        {
            cullingResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(
                light.visibleLightIndex, i, cascadeCount, ratios, tileSize, light.nearPlaneOffset,
                out Matrix4x4 viewMatrix, out Matrix4x4 projectionMatrix,
                out ShadowSplitData splitData
            );
            float cullingFactor = Mathf.Max(0f, 0.8f - settings.directional.cascadeFade);
            splitData.shadowCascadeBlendCullingFactor = cullingFactor;
            shadowSettings.splitData = splitData;
            if (index == 0)
            {
                SetCascadeData(i, splitData.cullingSphere, tileSize);
            }
            int tileIndex = tileOffset + i;
            dirShadowMatrices[tileIndex] = ConvertToAtlasMatrix( // 用于 世界坐标 转 纹理坐标 在阴影贴图上采样.
                projectionMatrix * viewMatrix,
                SetTileViewport(tileIndex, split, tileSize),
                split
            );
            buffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
            ExecuteBuffer();
            context.DrawShadows(ref shadowSettings);
            buffer.SetGlobalDepthBias(0f, 0f);
        }
    }

    Vector2 SetTileViewport(int index, int split, float tileSize)
    {
        Vector2 offset = new Vector2(index % split, index / split);
        buffer.SetViewport(new Rect(
            offset.x * tileSize, offset.y * tileSize, tileSize, tileSize
        ));
        return offset;
    }

    // Unity 剪裁空间 与OpenGL相同 x,y,z 除以 w 后 都是 [-1,1].
    // 但 z buffer 与 采样时用的 纹理坐标(uv) 中存储的是 [0,1].
    // 注意到 阴影贴图 是在正交视角下渲染的, 所以无需 透视除法,
    Matrix4x4 ConvertToAtlasMatrix(Matrix4x4 m, Vector2 offset, int split)
    {
        // 无论是正交还是透视, 这里应该都可以直接取负得到 Reversed Z
        // 可以参照 https://docs.unity.cn/2019.4/Documentation/Manual/SL-PlatformDifferences.html
        // 即手册中的 Graphics / Meshes, Materials, Shaders and Textures / Writing Shaders / Shader Reference / Advanced ShaderLab topics / Platform-specific rendering differences
        if (SystemInfo.usesReversedZBuffer)
        {
            m.m20 = -m.m20;
            m.m21 = -m.m21;
            m.m22 = -m.m22;
            m.m23 = -m.m23;
        }
        float scale = 1f / split;
        m.m00 = (0.5f * (m.m00 + m.m30) + offset.x * m.m30) * scale;
        m.m01 = (0.5f * (m.m01 + m.m31) + offset.x * m.m31) * scale;
        m.m02 = (0.5f * (m.m02 + m.m32) + offset.x * m.m32) * scale;
        m.m03 = (0.5f * (m.m03 + m.m33) + offset.x * m.m33) * scale;
        m.m10 = (0.5f * (m.m10 + m.m30) + offset.y * m.m30) * scale;
        m.m11 = (0.5f * (m.m11 + m.m31) + offset.y * m.m31) * scale;
        m.m12 = (0.5f * (m.m12 + m.m32) + offset.y * m.m32) * scale;
        m.m13 = (0.5f * (m.m13 + m.m33) + offset.y * m.m33) * scale;
        m.m20 = 0.5f * (m.m20 + m.m30);
        m.m21 = 0.5f * (m.m21 + m.m31);
        m.m22 = 0.5f * (m.m22 + m.m32);
        m.m23 = 0.5f * (m.m23 + m.m33);
        return m;
    }

    void SetCascadeData(int index, Vector4 cullingSphere, float tileSize)
    {
        float texelSize = 2f * cullingSphere.w / tileSize;
        float filterSize = texelSize * ((float)settings.directional.filter + 1f);
        cullingSphere.w -= filterSize;
        cullingSphere.w *= cullingSphere.w;
        cascadeCullingSpheres[index] = cullingSphere;
        cascadeData[index] = new Vector4(
            1f / cullingSphere.w,
            filterSize * 1.4142136f
        );
    }

    void SetKeywords(string[] keywords, int enabledIndex)
    {
        for (int i = 0; i < keywords.Length; i++)
        {
            if (i == enabledIndex)
            {
                buffer.EnableShaderKeyword(keywords[i]);
            }
            else
            {
                buffer.DisableShaderKeyword(keywords[i]);
            }
        }
    }

    public void Cleanup()
    {
        buffer.ReleaseTemporaryRT(dirShadowAtlasId);
        ExecuteBuffer();
    }
}