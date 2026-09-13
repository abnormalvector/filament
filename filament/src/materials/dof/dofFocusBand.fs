/*
 * Copyright (C) 2026 Abnormal Vector
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

// Signed circle-of-confusion diameter, compatible with the photographic path.
highp float focusCoc(highp float depth, highp vec2 textureUv) {
    if (materialParams.focusBandParams.w < 0.5) {
        return getCOC(depth, materialParams.cocParams);
    }
    // The background has no finite surface position. Always classify sky as far.
    if (depth <= 0.0) {
        return 2.0 * materialParams.cocClamp.y;
    }
    highp vec2 uv = uvToRenderTargetUV(textureUv);
    highp vec4 clip = vec4(uv * 2.0 - 1.0, depth, 1.0);
    highp float w = dot(materialParams.focusBandW, clip);
    highp float distance = dot(materialParams.focusBandPlane, clip) / max(w, 1e-8);
    highp float near = saturate((-distance - materialParams.focusBandParams.x) *
            materialParams.focusBandParams.y);
    highp float far = saturate((distance - materialParams.focusBandParams.x) *
            materialParams.focusBandParams.z);
    near = near * near * (3.0 - 2.0 * near);
    far = far * far * (3.0 - 2.0 * far);
    return 2.0 * dot(vec2(near, far), materialParams.cocClamp);
}
