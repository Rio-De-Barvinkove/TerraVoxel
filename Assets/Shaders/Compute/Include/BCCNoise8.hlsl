//
// BCC8 Noise - K.jpg's Smooth Re-oriented 8-Point BCC Noise
// From https://github.com/KdotJPG/New-Simplex-Style-Gradient-Noise
//

#ifndef _INCLUDE_BCCNOISE8_HLSL_
#define _INCLUDE_BCCNOISE8_HLSL_

float4 bcc8_mod(float4 x, float4 y) { return x - y * floor(x / y); }

float4 bcc8_permute(float4 t) {
    return t * (t * 34.0 + 133.0);
}

float3 bcc8_grad(float hash) {
    float3 cube = frac(floor(hash / float3(1, 2, 4)) * 0.5) * 4 - 1;
    float3 cuboct = cube;
    cuboct *= int3(0, 1, 2) != (int)(hash / 16);
    float type = frac(floor(hash / 8) * 0.5) * 2;
    float3 rhomb = (1.0 - type) * cube + type * (cuboct + cross(cube, cuboct));
    float3 grad = cuboct * 1.22474487139 + rhomb;
    grad *= (1.0 - 0.042942436724648037 * type) * 3.5946317686139184;
    return grad;
}

float4 Bcc8NoiseBase(float3 X) {
    float3 b = floor(X);
    float4 i4 = float4(X - b, 2.5);
    float3 v1 = b + floor(dot(i4, .25));
    float3 v2 = b + float3(1, 0, 0) + float3(-1, 1, 1) * floor(dot(i4, float4(-.25, .25, .25, .35)));
    float3 v3 = b + float3(0, 1, 0) + float3(1, -1, 1) * floor(dot(i4, float4(.25, -.25, .25, .35)));
    float3 v4 = b + float3(0, 0, 1) + float3(1, 1, -1) * floor(dot(i4, float4(.25, .25, -.25, .35)));
    float4 hashes = bcc8_permute(bcc8_mod(float4(v1.x, v2.x, v3.x, v4.x), 289.0));
    hashes = bcc8_permute(bcc8_mod(hashes + float4(v1.y, v2.y, v3.y, v4.y), 289.0));
    hashes = bcc8_mod(bcc8_permute(bcc8_mod(hashes + float4(v1.z, v2.z, v3.z, v4.z), 289.0)), 48.0);
    float3 d1 = X - v1; float3 d2 = X - v2; float3 d3 = X - v3; float3 d4 = X - v4;
    float4 a = max(0.75 - float4(dot(d1, d1), dot(d2, d2), dot(d3, d3), dot(d4, d4)), 0.0);
    float4 aa = a * a; float4 aaaa = aa * aa;
    float3 g1 = bcc8_grad(hashes.x); float3 g2 = bcc8_grad(hashes.y);
    float3 g3 = bcc8_grad(hashes.z); float3 g4 = bcc8_grad(hashes.w);
    float4 extrapolations = float4(dot(d1, g1), dot(d2, g2), dot(d3, g3), dot(d4, g4));
    float3 derivative = -8.0 * mul(aa * a * extrapolations, float4x3(d1, d2, d3, d4))
        + mul(aaaa, float4x3(g1, g2, g3, g4));
    return float4(derivative, dot(aaaa, extrapolations));
}

float4 Bcc8NoiseClassic(float3 X) {
    X = dot(X, 2.0/3.0) - X;
    float4 result = Bcc8NoiseBase(X) + Bcc8NoiseBase(X + 144.5);
    return float4(dot(result.xyz, 2.0/3.0) - result.xyz, result.w);
}

float Bcc8NoisePlaneFirstValue(float3 X) {
    float3x3 orthonormalMap = float3x3(
        0.788675134594813, -0.211324865405187, -0.577350269189626,
        -0.211324865405187, 0.788675134594813, -0.577350269189626,
        0.577350269189626, 0.577350269189626, 0.577350269189626);
    X = mul(X, orthonormalMap);
    float4 result = Bcc8NoiseBase(X) + Bcc8NoiseBase(X + 144.5);
    return result.w;
}

#endif
