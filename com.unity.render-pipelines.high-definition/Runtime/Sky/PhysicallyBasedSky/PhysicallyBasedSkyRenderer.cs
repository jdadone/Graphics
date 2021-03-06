using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    class PhysicallyBasedSkyRenderer : SkyRenderer
    {
        [GenerateHLSL]
        public enum PbrSkyConfig
        {
            // Tiny
            GroundIrradianceTableSize     = 256, // <N, L>

            // 32 MiB
            InScatteredRadianceTableSizeX = 128, // <N, V>
            InScatteredRadianceTableSizeY = 32,  // height
            InScatteredRadianceTableSizeZ = 16,  // AzimuthAngle(L) w.r.t. the view vector
            InScatteredRadianceTableSizeW = 64,  // <N, L>,
        }

        [GenerateHLSL(needAccessors = false, generateCBuffer = true, constantRegister = (int)ConstantRegister.PBRSky)]
        unsafe struct ShaderVariablesPhysicallyBasedSky
        {
            // All the distance-related entries use SI units (meter, 1/meter, etc).
            public float _PlanetaryRadius;
            public float _RcpPlanetaryRadius;
            public float _AtmosphericDepth;
            public float _RcpAtmosphericDepth;

            public float _AtmosphericRadius;
            public float _AerosolAnisotropy;
            public float _AerosolPhasePartConstant;
            public float _Unused;

            public float _AirDensityFalloff;
            public float _AirScaleHeight;
            public float _AerosolDensityFalloff;
            public float _AerosolScaleHeight;

            public Vector3 _AirSeaLevelExtinction;
            public float _AerosolSeaLevelExtinction;

            public Vector3 _AirSeaLevelScattering;
            public float _IntensityMultiplier;

            public Vector3 _AerosolSeaLevelScattering;
            public float _ColorSaturation;

            public Vector3 _GroundAlbedo;
            public float _AlphaSaturation;

            public Vector3 _PlanetCenterPosition; // Not used during the precomputation, but needed to apply the atmospheric effect
            public float _AlphaMultiplier;

            public Vector3 _HorizonTint;
            public float _HorizonZenithShiftPower;

            public Vector3 _ZenithTint;
            public float _HorizonZenithShiftScale;
        }

        // Store the hash of the parameters each time precomputation is done.
        // If the hash does not match, we must recompute our data.
        int m_LastPrecomputationParamHash;

        // We compute at most one bounce per frame for perf reasons.
        // We need to store the frame index because more than one render can happen during a frame (cubemap update + regular rendering).
        int m_LastPrecomputedBounce;

        // Precomputed data below.
        RTHandle[]                   m_GroundIrradianceTables;    // All orders, one order
        RTHandle[]                   m_InScatteredRadianceTables; // Air SS, Aerosol SS, Atmosphere MS, Atmosphere one order, Temp

        static ComputeShader         s_GroundIrradiancePrecomputationCS;
        static ComputeShader         s_InScatteredRadiancePrecomputationCS;
        Material                     s_PbrSkyMaterial;
        static MaterialPropertyBlock s_PbrSkyMaterialProperties;

        ShaderVariablesPhysicallyBasedSky m_ConstantBuffer;
        int m_ShaderVariablesPhysicallyBasedSkyID = Shader.PropertyToID("ShaderVariablesPhysicallyBasedSky");

        static GraphicsFormat s_ColorFormat = GraphicsFormat.R16G16B16A16_SFloat;

        RTHandle AllocateGroundIrradianceTable(int index)
        {
            var table = RTHandles.Alloc((int)PbrSkyConfig.GroundIrradianceTableSize, 1,
                                        colorFormat: s_ColorFormat,
                                        enableRandomWrite: true,
                                        name: string.Format("GroundIrradianceTable{0}", index));

            Debug.Assert(table != null);

            return table;
        }

        RTHandle AllocateInScatteredRadianceTable(int index)
        {
            // Emulate a 4D texture with a "deep" 3D texture.
            var table = RTHandles.Alloc((int)PbrSkyConfig.InScatteredRadianceTableSizeX,
                                        (int)PbrSkyConfig.InScatteredRadianceTableSizeY,
                                        (int)PbrSkyConfig.InScatteredRadianceTableSizeZ *
                                        (int)PbrSkyConfig.InScatteredRadianceTableSizeW,
                                        dimension: TextureDimension.Tex3D,
                                        colorFormat: s_ColorFormat,
                                        enableRandomWrite: true,
                                        name: string.Format("InScatteredRadianceTable{0}", index));

            Debug.Assert(table != null);

            return table;
        }

        public PhysicallyBasedSkyRenderer()
        {
        }

        public override void Build()
        {
            var hdrpAsset     = HDRenderPipeline.currentAsset;
            var hdrpResources = HDRenderPipeline.defaultAsset.renderPipelineResources;

            // Shaders
            s_GroundIrradiancePrecomputationCS    = hdrpResources.shaders.groundIrradiancePrecomputationCS;
            s_InScatteredRadiancePrecomputationCS = hdrpResources.shaders.inScatteredRadiancePrecomputationCS;
            s_PbrSkyMaterialProperties            = new MaterialPropertyBlock();

            s_PbrSkyMaterial = CoreUtils.CreateEngineMaterial(hdrpResources.shaders.physicallyBasedSkyPS);

            Debug.Assert(s_GroundIrradiancePrecomputationCS    != null);
            Debug.Assert(s_InScatteredRadiancePrecomputationCS != null);

            // No temp tables.
            m_GroundIrradianceTables       = new RTHandle[2];
            m_GroundIrradianceTables[0]    = AllocateGroundIrradianceTable(0);

            m_InScatteredRadianceTables    = new RTHandle[5];
            m_InScatteredRadianceTables[0] = AllocateInScatteredRadianceTable(0);
            m_InScatteredRadianceTables[1] = AllocateInScatteredRadianceTable(1);
            m_InScatteredRadianceTables[2] = AllocateInScatteredRadianceTable(2);
        }

        public override void SetGlobalSkyData(CommandBuffer cmd, BuiltinSkyParameters builtinParams)
        {
            UpdateGlobalConstantBuffer(cmd, builtinParams);

            // TODO: ground irradiance table? Volume SH? Something else?
            if (m_LastPrecomputedBounce > 0)
            {
                cmd.SetGlobalTexture(HDShaderIDs._AirSingleScatteringTexture,     m_InScatteredRadianceTables[0]);
                cmd.SetGlobalTexture(HDShaderIDs._AerosolSingleScatteringTexture, m_InScatteredRadianceTables[1]);
                cmd.SetGlobalTexture(HDShaderIDs._MultipleScatteringTexture,      m_InScatteredRadianceTables[2]);
            }
            else
            {
                cmd.SetGlobalTexture(HDShaderIDs._AirSingleScatteringTexture, CoreUtils.blackVolumeTexture);
                cmd.SetGlobalTexture(HDShaderIDs._AerosolSingleScatteringTexture, CoreUtils.blackVolumeTexture);
                cmd.SetGlobalTexture(HDShaderIDs._MultipleScatteringTexture, CoreUtils.blackVolumeTexture);
            }

        }

        public override void Cleanup()
        {
            RTHandles.Release(m_GroundIrradianceTables[0]);    m_GroundIrradianceTables[0]    = null;
            RTHandles.Release(m_GroundIrradianceTables[1]);    m_GroundIrradianceTables[1]    = null;
            RTHandles.Release(m_InScatteredRadianceTables[0]); m_InScatteredRadianceTables[0] = null;
            RTHandles.Release(m_InScatteredRadianceTables[1]); m_InScatteredRadianceTables[1] = null;
            RTHandles.Release(m_InScatteredRadianceTables[2]); m_InScatteredRadianceTables[2] = null;
            RTHandles.Release(m_InScatteredRadianceTables[3]); m_InScatteredRadianceTables[3] = null;
            RTHandles.Release(m_InScatteredRadianceTables[4]); m_InScatteredRadianceTables[4] = null;

            CoreUtils.Destroy(s_PbrSkyMaterial);

            m_LastPrecomputedBounce = 0;
        }

        static float CornetteShanksPhasePartConstant(float anisotropy)
        {
            float g = anisotropy;

            return (3.0f / (8.0f * Mathf.PI)) * (1.0f - g * g) / (2.0f + g * g);
        }

        static Vector2 ComputeExponentialInterpolationParams(float k)
        {
            if (k == 0) k = 1e-6f; // Avoid the numerical explosion around 0

            // Remap t: (exp(10 k t) - 1) / (exp(10 k) - 1) = exp(x t) y - y.
            float x = 10 * k;
            float y = 1 / (Mathf.Exp(x) - 1);

            return new Vector2(x, y);
        }

        // For both precomputation and runtime lighting passes.
        void UpdateGlobalConstantBuffer(CommandBuffer cmd, BuiltinSkyParameters builtinParams)
        {
            var pbrSky = builtinParams.skySettings as PhysicallyBasedSky;

            float R    = pbrSky.GetPlanetaryRadius();
            float D    = Mathf.Max(pbrSky.airMaximumAltitude.value, pbrSky.aerosolMaximumAltitude.value);
            float airH = pbrSky.GetAirScaleHeight();
            float aerH = pbrSky.GetAerosolScaleHeight();
            float iMul = GetSkyIntensity(pbrSky, builtinParams.debugSettings);

            Vector2 expParams = ComputeExponentialInterpolationParams(pbrSky.horizonZenithShift.value);

            m_ConstantBuffer._PlanetaryRadius           = R;
            m_ConstantBuffer._RcpPlanetaryRadius        = 1.0f / R;
            m_ConstantBuffer._AtmosphericDepth          = D;
            m_ConstantBuffer._RcpAtmosphericDepth       = 1.0f / D;

            m_ConstantBuffer._AtmosphericRadius         = R + D;
            m_ConstantBuffer._AerosolAnisotropy         = pbrSky.aerosolAnisotropy.value;
            m_ConstantBuffer._AerosolPhasePartConstant  = CornetteShanksPhasePartConstant(pbrSky.aerosolAnisotropy.value);
            m_ConstantBuffer._Unused                    = 0.0f; // Warning fix

            m_ConstantBuffer._AirDensityFalloff         = 1.0f / airH;
            m_ConstantBuffer._AirScaleHeight            = airH;
            m_ConstantBuffer._AerosolDensityFalloff     = 1.0f / aerH;
            m_ConstantBuffer._AerosolScaleHeight        = aerH;

            m_ConstantBuffer._AirSeaLevelExtinction     = pbrSky.GetAirExtinctionCoefficient();
            m_ConstantBuffer._AerosolSeaLevelExtinction = pbrSky.GetAerosolExtinctionCoefficient();

            m_ConstantBuffer._AirSeaLevelScattering     = pbrSky.GetAirScatteringCoefficient();
            m_ConstantBuffer._IntensityMultiplier       = iMul;

            m_ConstantBuffer._AerosolSeaLevelScattering = pbrSky.GetAerosolScatteringCoefficient();
            m_ConstantBuffer._ColorSaturation           = pbrSky.colorSaturation.value;

            Vector3 groundAlbedo = new Vector3(pbrSky.groundTint.value.r, pbrSky.groundTint.value.g, pbrSky.groundTint.value.b);
            m_ConstantBuffer._GroundAlbedo              = groundAlbedo;
            m_ConstantBuffer._AlphaSaturation           = pbrSky.alphaSaturation.value;

            m_ConstantBuffer._PlanetCenterPosition      = pbrSky.GetPlanetCenterPosition(builtinParams.worldSpaceCameraPos);
            m_ConstantBuffer._AlphaMultiplier           = pbrSky.alphaMultiplier.value;

            Vector3 horizonTint = new Vector3(pbrSky.horizonTint.value.r, pbrSky.horizonTint.value.g, pbrSky.horizonTint.value.b);
            m_ConstantBuffer._HorizonTint               = horizonTint;
            m_ConstantBuffer._HorizonZenithShiftPower   = expParams.x;

            Vector3 zenithTint = new Vector3(pbrSky.zenithTint.value.r, pbrSky.zenithTint.value.g, pbrSky.zenithTint.value.b);
            m_ConstantBuffer._ZenithTint                = zenithTint;
            m_ConstantBuffer._HorizonZenithShiftScale   = expParams.y;

            ConstantBuffer.PushGlobal(cmd, m_ConstantBuffer, m_ShaderVariablesPhysicallyBasedSkyID);
        }

        void PrecomputeTables(CommandBuffer cmd)
        {
            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.InScatteredRadiancePrecomputation)))
            {
                int order = m_LastPrecomputedBounce + 1;
                {
                    // For efficiency reasons, multiple scattering is computed in 2 passes:
                    // 1. Gather the in-scattered radiance over the entire sphere of directions.
                    // 2. Accumulate the in-scattered radiance along the ray.
                    // Single scattering performs both steps during the same pass.

                    int firstPass = Math.Min(order - 1, 2);
                    int accumPass = 3;
                    int numPasses = Math.Min(order, 2);

                    for (int i = 0; i < numPasses; i++)
                    {
                        int pass = (i == 0) ? firstPass : accumPass;

                        switch (pass)
                        {
                        case 0:
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._AirSingleScatteringTable,       m_InScatteredRadianceTables[0]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._AerosolSingleScatteringTable,   m_InScatteredRadianceTables[1]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._MultipleScatteringTable,        m_InScatteredRadianceTables[2]); // MS orders
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._MultipleScatteringTableOrder,   m_InScatteredRadianceTables[3]); // One order
                            break;
                        case 1:
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._AirSingleScatteringTexture,     m_InScatteredRadianceTables[0]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._AerosolSingleScatteringTexture, m_InScatteredRadianceTables[1]);
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._GroundIrradianceTexture,        m_GroundIrradianceTables[1]);    // One order
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._MultipleScatteringTable,        m_InScatteredRadianceTables[4]); // Temp
                            break;
                        case 2:
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._MultipleScatteringTexture,      m_InScatteredRadianceTables[3]); // One order
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._GroundIrradianceTexture,        m_GroundIrradianceTables[1]);    // One order
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._MultipleScatteringTable,        m_InScatteredRadianceTables[4]); // Temp
                            break;
                        case 3:
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._MultipleScatteringTexture,      m_InScatteredRadianceTables[4]); // Temp
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._MultipleScatteringTableOrder,   m_InScatteredRadianceTables[3]); // One order
                            cmd.SetComputeTextureParam(s_InScatteredRadiancePrecomputationCS, pass, HDShaderIDs._MultipleScatteringTable,        m_InScatteredRadianceTables[2]); // MS orders
                            break;
                        default:
                            Debug.Assert(false);
                            break;
                        }

                        // Re-illuminate the sky with each bounce.
                        // Emulate a 4D dispatch with a "deep" 3D dispatch.
                        cmd.DispatchCompute(s_InScatteredRadiancePrecomputationCS, pass, (int)PbrSkyConfig.InScatteredRadianceTableSizeX / 4,
                                                                                         (int)PbrSkyConfig.InScatteredRadianceTableSizeY / 4,
                                                                                         (int)PbrSkyConfig.InScatteredRadianceTableSizeZ / 4 *
                                                                                         (int)PbrSkyConfig.InScatteredRadianceTableSizeW);
                    }

                    {
                        // Used by all passes.
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, HDShaderIDs._GroundIrradianceTable,          m_GroundIrradianceTables[0]); // All orders
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, HDShaderIDs._GroundIrradianceTableOrder,     m_GroundIrradianceTables[1]); // One order
                    }

                    switch (firstPass)
                    {
                    case 0:
                        break;
                    case 1:
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, HDShaderIDs._AirSingleScatteringTexture,     m_InScatteredRadianceTables[0]);
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, HDShaderIDs._AerosolSingleScatteringTexture, m_InScatteredRadianceTables[1]);
                        break;
                    case 2:
                        cmd.SetComputeTextureParam(s_GroundIrradiancePrecomputationCS, firstPass, HDShaderIDs._MultipleScatteringTexture,      m_InScatteredRadianceTables[3]); // One order
                        break;
                    default:
                        Debug.Assert(false);
                        break;
                    }

                    // Re-illuminate the ground with each bounce.
                    cmd.DispatchCompute(s_GroundIrradiancePrecomputationCS, firstPass, (int)PbrSkyConfig.GroundIrradianceTableSize / 64, 1, 1);
                }
            }
        }

        protected override bool Update(BuiltinSkyParameters builtinParams)
        {
            UpdateGlobalConstantBuffer(builtinParams.commandBuffer, builtinParams);
            var pbrSky = builtinParams.skySettings as PhysicallyBasedSky;

            int currPrecomputationParamHash = pbrSky.GetPrecomputationHashCode();
            if (currPrecomputationParamHash != m_LastPrecomputationParamHash)
            {
                // Hash does not match, have to restart the precomputation from scratch.
                m_LastPrecomputedBounce = 0;
            }

            if (m_LastPrecomputedBounce == 0)
            {
                // Allocate temp tables if needed
                if (m_GroundIrradianceTables[1] == null)
                {
                    m_GroundIrradianceTables[1] = AllocateGroundIrradianceTable(1);
                }

                if (m_InScatteredRadianceTables[3] == null)
                {
                    m_InScatteredRadianceTables[3] = AllocateInScatteredRadianceTable(3);
                }

                if (m_InScatteredRadianceTables[4] == null)
                {
                    m_InScatteredRadianceTables[4] = AllocateInScatteredRadianceTable(4);
                }
            }

            if (m_LastPrecomputedBounce == pbrSky.numberOfBounces.value)
            {
                // Free temp tables.
                // This is a deferred release (one frame late)!
                RTHandles.Release(m_GroundIrradianceTables[1]);
                RTHandles.Release(m_InScatteredRadianceTables[3]);
                RTHandles.Release(m_InScatteredRadianceTables[4]);
                m_GroundIrradianceTables[1] = null;
                m_InScatteredRadianceTables[3] = null;
                m_InScatteredRadianceTables[4] = null;
            }

            if (m_LastPrecomputedBounce < pbrSky.numberOfBounces.value)
            {
                PrecomputeTables(builtinParams.commandBuffer);
                m_LastPrecomputedBounce++;

                // Update the hash for the current bounce.
                m_LastPrecomputationParamHash = currPrecomputationParamHash;

                // If the sky is realtime, an upcoming update will update the sky lighting. Otherwise we need to force an update.
                return builtinParams.skySettings.updateMode != EnvironmentUpdateMode.Realtime;
            }

            return false;
        }

        // 'renderSunDisk' parameter is not supported.
        // Users should instead create an emissive (or lit) mesh for every relevant light source
        // (to support multiple stars in space, moons with moon phases, etc).
        public override void RenderSky(BuiltinSkyParameters builtinParams, bool renderForCubemap, bool renderSunDisk)
        {
            var pbrSky = builtinParams.skySettings as PhysicallyBasedSky;

            // TODO: the following expression is somewhat inefficient, but good enough for now.
            Vector3 X = builtinParams.worldSpaceCameraPos;
            float   r = Vector3.Distance(X, pbrSky.GetPlanetCenterPosition(X));
            float   R = pbrSky.GetPlanetaryRadius();

            bool isPbrSkyActive = r > R; // Disable sky rendering below the ground

            CommandBuffer cmd = builtinParams.commandBuffer;

            // Precomputation is done, shading is next.
            Quaternion planetRotation = Quaternion.Euler(pbrSky.planetRotation.value.x,
                                                         pbrSky.planetRotation.value.y,
                                                         pbrSky.planetRotation.value.z);

            Quaternion spaceRotation  = Quaternion.Euler(pbrSky.spaceRotation.value.x,
                                                         pbrSky.spaceRotation.value.y,
                                                         pbrSky.spaceRotation.value.z);

            s_PbrSkyMaterialProperties.SetMatrix(HDShaderIDs._PixelCoordToViewDirWS, builtinParams.pixelCoordToViewDirMatrix);
            s_PbrSkyMaterialProperties.SetVector(HDShaderIDs._WorldSpaceCameraPos1,  builtinParams.worldSpaceCameraPos);
            s_PbrSkyMaterialProperties.SetMatrix(HDShaderIDs._ViewMatrix1,           builtinParams.viewMatrix);
            s_PbrSkyMaterialProperties.SetMatrix(HDShaderIDs._PlanetRotation,        Matrix4x4.Rotate(planetRotation));
            s_PbrSkyMaterialProperties.SetMatrix(HDShaderIDs._SpaceRotation,         Matrix4x4.Rotate(spaceRotation));

            if (m_LastPrecomputedBounce != 0)
            {
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._GroundIrradianceTexture,        m_GroundIrradianceTables[0]);
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._AirSingleScatteringTexture,     m_InScatteredRadianceTables[0]);
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._AerosolSingleScatteringTexture, m_InScatteredRadianceTables[1]);
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._MultipleScatteringTexture,      m_InScatteredRadianceTables[2]);
            }
            else
            {
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._GroundIrradianceTexture,        Texture2D.blackTexture);
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._AirSingleScatteringTexture,     CoreUtils.blackVolumeTexture);
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._AerosolSingleScatteringTexture, CoreUtils.blackVolumeTexture);
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._MultipleScatteringTexture,      CoreUtils.blackVolumeTexture);
            }

            int hasGroundAlbedoTexture = 0;

            if (pbrSky.groundColorTexture.value != null)
            {
                hasGroundAlbedoTexture = 1;
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._GroundAlbedoTexture, pbrSky.groundColorTexture.value);
            }
            s_PbrSkyMaterialProperties.SetInt(HDShaderIDs._HasGroundAlbedoTexture, hasGroundAlbedoTexture);

            int hasGroundEmissionTexture = 0;

            if (pbrSky.groundEmissionTexture.value != null)
            {
                hasGroundEmissionTexture = 1;
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._GroundEmissionTexture,    pbrSky.groundEmissionTexture.value);
                s_PbrSkyMaterialProperties.SetFloat(  HDShaderIDs._GroundEmissionMultiplier, pbrSky.groundEmissionMultiplier.value);
            }
            s_PbrSkyMaterialProperties.SetInt(HDShaderIDs._HasGroundEmissionTexture, hasGroundEmissionTexture);

            int hasSpaceEmissionTexture = 0;

            if (pbrSky.spaceEmissionTexture.value != null)
            {
                hasSpaceEmissionTexture = 1;
                s_PbrSkyMaterialProperties.SetTexture(HDShaderIDs._SpaceEmissionTexture,    pbrSky.spaceEmissionTexture.value);
                s_PbrSkyMaterialProperties.SetFloat(  HDShaderIDs._SpaceEmissionMultiplier, pbrSky.spaceEmissionMultiplier.value);
            }
            s_PbrSkyMaterialProperties.SetInt(HDShaderIDs._HasSpaceEmissionTexture, hasSpaceEmissionTexture);

            s_PbrSkyMaterialProperties.SetInt(HDShaderIDs._RenderSunDisk, renderSunDisk ? 1 : 0);

            int pass = (renderForCubemap ? 0 : 2) + (isPbrSkyActive ? 0 : 1);

            CoreUtils.DrawFullScreen(builtinParams.commandBuffer, s_PbrSkyMaterial, s_PbrSkyMaterialProperties, pass);
        }
    }
}
