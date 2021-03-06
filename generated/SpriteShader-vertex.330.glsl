#version 330 core

struct SamplerDummy { int _dummyValue; };

struct OpenWheels_Veldrid_Shaders_SpriteShader_VertexInput
{
    vec3 Position;
    vec4 Color;
    vec2 TextureCoordinates;
};

struct OpenWheels_Veldrid_Shaders_SpriteShader_FragmentInput
{
    vec4 Position;
    vec4 Color;
    vec2 TextureCoordinates;
};

layout(std140) uniform Wvp
{
    mat4 field_Wvp;
};


OpenWheels_Veldrid_Shaders_SpriteShader_FragmentInput VS( OpenWheels_Veldrid_Shaders_SpriteShader_VertexInput input_)
{
    OpenWheels_Veldrid_Shaders_SpriteShader_FragmentInput output_;
    output_.Position = field_Wvp * vec4(input_.Position, 1);
    output_.Color = input_.Color;
    output_.TextureCoordinates = input_.TextureCoordinates;
    return output_;
}


in vec3 Position;
in vec4 Color;
in vec2 TextureCoordinates;
out vec4 fsin_0;
out vec2 fsin_1;

void main()
{
    OpenWheels_Veldrid_Shaders_SpriteShader_VertexInput input_;
    input_.Position = Position;
    input_.Color = Color;
    input_.TextureCoordinates = TextureCoordinates;
    OpenWheels_Veldrid_Shaders_SpriteShader_FragmentInput output_ = VS(input_);
    fsin_0 = output_.Color;
    fsin_1 = output_.TextureCoordinates;
    gl_Position = output_.Position;
        gl_Position.z = gl_Position.z * 2.0 - gl_Position.w;
}
