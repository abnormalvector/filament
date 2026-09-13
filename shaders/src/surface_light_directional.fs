//------------------------------------------------------------------------------
// Directional light evaluation
//------------------------------------------------------------------------------

#if FILAMENT_QUALITY >= FILAMENT_QUALITY_HIGH
#define SUN_AS_AREA_LIGHT
#endif

vec3 sampleSunAreaLight(const vec3 lightDirection) {
#if defined(SUN_AS_AREA_LIGHT)
    if (frameUniforms.sun.w >= 0.0) {
        // simulate sun as disc area light
        float LoR = dot(lightDirection, shading_reflected);
        float d = frameUniforms.sun.x;
        highp vec3 s = shading_reflected - LoR * lightDirection;
        return LoR < d ?
                normalize(lightDirection * d + normalize(s) * frameUniforms.sun.y) : shading_reflected;
    }
#endif
    return lightDirection;
}

#if MATERIAL_FEATURE_LEVEL > 0
highp float cloudShadowHash(highp vec2 cell) {
    // Periodic integer lattice permits continuous, bounded CPU animation offsets.
    highp uvec2 p = uvec2(mod(cell, 256.0));
    highp uint h = p.x * 1664525u + p.y * 1013904223u;
    h = (h ^ (h >> 16u)) * 2246822519u;
    h ^= h >> 13u;
    return float(h & 0x00ffffffu) * (1.0 / 16777215.0);
}

highp float cloudShadowNoise(highp vec2 p) {
    highp vec2 cell = floor(p);
    highp vec2 f = fract(p);
    highp vec2 w = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return mix(mix(cloudShadowHash(cell), cloudShadowHash(cell + vec2(1.0, 0.0)), w.x),
               mix(cloudShadowHash(cell + vec2(0.0, 1.0)),
                   cloudShadowHash(cell + vec2(1.0)), w.x), w.y);
}

float cloudShadowTransmittance() {
    highp vec4 parameters = frameUniforms.cloudShadowParams;
    if (parameters.x <= 0.0) {
        return 1.0;
    }
    // Undo Filament's camera-origin shift and IBL rotation for both position and light.
    highp vec3 position = getUserWorldPosition();
    highp vec3 sun = mat3(getUserWorldFromWorldMatrix()) * frameUniforms.lightDirection;
    // Fade out at the horizon, where projection onto the ground becomes singular.
    float horizon = smoothstep(0.02, 0.1, sun.y);
    if (horizon <= 0.0) {
        return 1.0;
    }
    highp vec2 projected = position.xz - position.y * sun.xz / max(sun.y, 0.02);
    highp vec2 uv = projected * parameters.y - frameUniforms.cloudShadowOffset.xy;
    highp float density = 0.65 * cloudShadowNoise(uv)
            + 0.25 * cloudShadowNoise(uv * 2.0 + vec2(17.0, 43.0))
            + 0.10 * cloudShadowNoise(uv * 4.0 + vec2(61.0, 13.0));
    float threshold = mix(1.0 + parameters.w, -parameters.w, parameters.z);
    float cloud = smoothstep(threshold - parameters.w, threshold + parameters.w, density);
    return 1.0 - parameters.x * cloud * horizon;
}
#endif

Light getDirectionalLight() {
    Light light;
    // note: lightColorIntensity.w is always premultiplied by the exposure
    light.colorIntensity = frameUniforms.lightColorIntensity;
#if MATERIAL_FEATURE_LEVEL > 0
    light.colorIntensity.w *= cloudShadowTransmittance();
#endif
    light.l = sampleSunAreaLight(frameUniforms.lightDirection);
    light.attenuation = 1.0;
    light.NoL = saturate(dot(shading_normal, light.l));
    light.channels = frameUniforms.lightChannels & 0xFF;
    return light;
}

void evaluateDirectionalLight(const MaterialInputs material,
        const PixelParams pixel, inout vec3 color) {

    Light light = getDirectionalLight();

    int channels = object_uniforms_flagsChannels & 0xFF;
    if ((light.channels & channels) == 0) {
        return;
    }

#if defined(MATERIAL_CAN_SKIP_LIGHTING)
    if (light.NoL <= 0.0) {
        return;
    }
#endif

    float visibility = 1.0;
#if defined(VARIANT_HAS_SHADOWING)
    if (light.NoL > 0.0) {
        float ssContactShadowOcclusion = 0.0;

        int cascade = getShadowCascade();
        bool cascadeHasVisibleShadows = bool(frameUniforms.cascades & ((1 << cascade) << 8));
        bool hasDirectionalShadows = bool(frameUniforms.directionalShadows & 1);
        if (hasDirectionalShadows && cascadeHasVisibleShadows) {
            highp vec4 shadowPosition = getShadowPosition(cascade);
            visibility = shadow(true, sampler0_shadowMap, cascade, shadowPosition, 0.0);
#if defined(MATERIAL_HAS_SHADOW_STRENGTH)
            applyShadowStrength(visibility, material.shadowStrength);
#endif
#if defined (FILAMENT_SHADOW_FAR_ATTENUATION)
            // shadow far attenuation
            highp vec3 v = getWorldPosition() - getWorldCameraPosition();
            // (viewFromWorld * v).z == dot(transpose(viewFromWorld), v)
            highp float z = dot(transpose(getViewFromWorldMatrix())[2].xyz, v);
            highp vec2 p = frameUniforms.shadowFarAttenuationParams;
            visibility = 1.0 - ((1.0 - visibility) * saturate(p.x - z * z * p.y));
#endif
        }
        if ((frameUniforms.directionalShadows & 0x2) != 0 && visibility > 0.0) {
            if ((object_uniforms_flagsChannels & FILAMENT_OBJECT_CONTACT_SHADOWS_BIT) != 0) {
                ssContactShadowOcclusion = screenSpaceContactShadow(light.l);
            }
        }

        visibility *= 1.0 - ssContactShadowOcclusion;

        #if defined(MATERIAL_HAS_AMBIENT_OCCLUSION)
        visibility *= computeMicroShadowing(light.NoL, material.ambientOcclusion);
        #endif
#if defined(MATERIAL_CAN_SKIP_LIGHTING)
        if (visibility <= 0.0) {
            return;
        }
#endif
    }
#endif

#if defined(MATERIAL_HAS_CUSTOM_SURFACE_SHADING)
    color.rgb += customSurfaceShading(material, pixel, light, visibility);
#else
    color.rgb += surfaceShading(pixel, light, visibility);
#endif
}

//------------------------------------------------------------------------------
// Extra directional lights evaluation
//
// Directional lights in addition to the dominant one. These are evaluated
// without shadows and without the sun disc.
//------------------------------------------------------------------------------

void evaluateExtraDirectionalLights(const MaterialInputs material,
        const PixelParams pixel, inout vec3 color) {
    int channels = object_uniforms_flagsChannels & 0xFF;
    // RUNTIME_CONFIG_HAS_EXTRA_DIRECTIONAL_LIGHTS is a dynamic specialization constant,
    // set automatically when the scene contains more than one directional light. It is
    // false by default, in which case this function compiles away entirely.
    if (!RUNTIME_CONFIG_HAS_EXTRA_DIRECTIONAL_LIGHTS) {
        return;
    }
    // At feature level 0, GLSL ES 1.00 only guarantees support for loops with a constant
    // bound, and only allows indexing a uniform array with such a loop's index, so we
    // iterate over the maximum and guard the body with the actual count. At higher feature
    // levels the loop is simply bounded by the count.
#if MATERIAL_FEATURE_LEVEL == 0
    for (int i = 0; i < CONFIG_MAX_EXTRA_DIRECTIONAL_LIGHTS; i++) {
        if (i < frameUniforms.extraLightCount) {
#else
    for (int i = 0; i < frameUniforms.extraLightCount; i++) {
#endif
            Light light;
            // note: colorIntensity.w is always premultiplied by the exposure
            light.colorIntensity = frameUniforms.extraLightColorIntensity[i];
            light.l = frameUniforms.extraLightDirection[i].xyz;
            light.attenuation = 1.0;
            light.NoL = saturate(dot(shading_normal, light.l));
            // the channel mask fits in 8 bits, so the float conversion is exact
            light.channels = int(frameUniforms.extraLightDirection[i].w);

            if ((light.channels & channels) != 0) {
#if defined(MATERIAL_CAN_SKIP_LIGHTING)
                if (light.NoL > 0.0) {
#endif
                    float visibility = 1.0;
#if defined(VARIANT_HAS_SHADOWING) && defined(MATERIAL_HAS_AMBIENT_OCCLUSION)
                    visibility = computeMicroShadowing(light.NoL, material.ambientOcclusion);
#endif
#if defined(MATERIAL_HAS_CUSTOM_SURFACE_SHADING)
                    color.rgb += customSurfaceShading(material, pixel, light, visibility);
#else
                    color.rgb += surfaceShading(pixel, light, visibility);
#endif
#if defined(MATERIAL_CAN_SKIP_LIGHTING)
                }
#endif
            }
#if MATERIAL_FEATURE_LEVEL == 0
        }
#endif
    }
}
