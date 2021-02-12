#ifndef CUSTOM_BRDF_INCLUDED
#define CUSTOM_BRDF_INCLUDED

struct BRDF {
    float3 diffuse;
    float3 specular;
    float roughness;
};


#define MIN_REFLECTIVITY 0.04

float OneMinusReflectivity (float metallic) {
    float range = 1.0 - MIN_REFLECTIVITY;
    return range - metallic * range;
}

BRDF GetBRDF (Surface surface) {
    BRDF brdf;

    brdf.diffuse = surface.color * OneMinusReflectivity(surface.metallic);
    brdf.specular = lerp(MIN_REFLECTIVITY, surface.color, surface.metallic);
    brdf.roughness = 1.0;
    return brdf;
}


#endif