shader_type canvas_item;

// Three separate textures for Y, U, and V channels
uniform sampler2D texture_y : hint_default_black, filter_linear;
uniform sampler2D texture_u : hint_default_black, filter_linear;
uniform sampler2D texture_v : hint_default_black, filter_linear;

void fragment() {
    // 1. Sample Y, U, and V (R8 format stores the value in the .r channel)
    float y = texture(texture_y, UV).r;
    float u = texture(texture_u, UV).r - 0.5; // Offset Chroma by -0.5
    float v = texture(texture_v, UV).r - 0.5;

    // 2. BT.601 YUV to RGB conversion matrix
    float r = y + 1.402 * v;
    float g = y - 0.344136 * u - 0.714136 * v;
    float b = y + 1.772 * u;

    // 3. Clamp final colors to valid 0.0 - 1.0 range
    COLOR = vec4(clamp(vec3(r, g, b), 0.0, 1.0), 1.0);
}