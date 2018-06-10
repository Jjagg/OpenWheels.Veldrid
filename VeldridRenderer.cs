using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenWheels.Rendering;
using Veldrid;
using Veldrid.ImageSharp;

namespace OpenWheels.Veldrid
{
    /// <summary>
    /// <see cref="IRenderer"/> implementation that uses Veldrid to render.
    /// </summary>
    public class VeldridRenderer : IRenderer, IDisposable
    {
        public GraphicsDevice GraphicsDevice { get; }
        private const int InitialTextureCount = 64;

        private readonly List<int> _freeIds;
        private readonly Dictionary<string, int> _textureIds;
        private Texture[] _textures;
        private TextureView[] _textureViews;
        private ResourceSet[] _textureResourceSets;

        private CommandList _commandList;
        private DeviceBuffer _vertexBuffer;
        private DeviceBuffer _indexBuffer;
        private Shader _vertexShader;
        private Shader _fragmentShader;
        private ShaderSetDescription _shaderSet;

        private ResourceLayout _wvpLayout;
        private ResourceLayout _textureLayout;
        private ResourceLayout[] _resourceLayouts;
        private ResourceSet _wvpSet;
        private DeviceBuffer _wvpBuffer;

        private Dictionary<GraphicsState, Pipeline> _pipelines;

        private bool _disposed;

        private Framebuffer _currentTarget;

        /// <summary>
        /// Create a new renderer.
        /// </summary>
        /// <param name="graphicsDevice">The <see cref="global::Veldrid.GraphicsDevice"/> to render with.</param>
        /// <exception cref="ArgumentNullException">If <paramref name="graphicsDevice"/> is <code>null</code>.</exception>
        public VeldridRenderer(GraphicsDevice graphicsDevice)
        {
            if (graphicsDevice == null)
                throw new ArgumentNullException(nameof(graphicsDevice));

            GraphicsDevice = graphicsDevice;
            _currentTarget = GraphicsDevice.SwapchainFramebuffer;

            _freeIds = new List<int>(InitialTextureCount);
            _freeIds.AddRange(Enumerable.Range(0, InitialTextureCount));
            _textureIds = new Dictionary<string, int>(InitialTextureCount);
            _textures = new Texture[InitialTextureCount];
            _textureViews = new TextureView[InitialTextureCount];
            _textureResourceSets = new ResourceSet[InitialTextureCount];

            CreateResources();
            _pipelines = new Dictionary<GraphicsState, Pipeline>();
        }

        private void CreateResources()
        {
            var rf = GraphicsDevice.ResourceFactory;

            var vbDescription = new BufferDescription(
                Batcher.InitialMaxVertices * Vertex.SizeInBytes,
                BufferUsage.VertexBuffer);
            _vertexBuffer = rf.CreateBuffer(ref vbDescription);
            var ibDescription = new BufferDescription(
                Batcher.InitialMaxIndices * sizeof(int),
                BufferUsage.IndexBuffer);
            _indexBuffer = rf.CreateBuffer(ref ibDescription);

            _commandList = rf.CreateCommandList();

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float3),
                new VertexElementDescription("Color", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm),
                new VertexElementDescription("TextureCoordinate", VertexElementSemantic.TextureCoordinate,
                    VertexElementFormat.Float2));

            _vertexShader = VeldridHelper.LoadShader(rf, "SpriteShader", ShaderStages.Vertex, "VS");
            _fragmentShader = VeldridHelper.LoadShader(rf, "SpriteShader", ShaderStages.Fragment, "FS");

            _shaderSet = new ShaderSetDescription(
                new[] {vertexLayout},
                new[] {_vertexShader, _fragmentShader});

            _wvpLayout = rf.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Wvp", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _textureLayout = rf.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Input", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("Sampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            _resourceLayouts = new[] {_wvpLayout, _textureLayout};

            _wvpBuffer = rf.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer));
            UpdateWvp();

            _wvpSet = rf.CreateResourceSet(new ResourceSetDescription(_wvpLayout, _wvpBuffer));
        }

        /// <summary>
        /// Set the render target.
        /// </summary>
        /// <param name="target">
        ///   <see cref="Framebuffer"/> to render to. Uses <see cref="Veldrid.GraphicsDevice.SwapchainFramebuffer"/>
        ///   of the
        /// </param>
        public void SetTarget(Framebuffer target)
        {
            if (target == null)
                target = GraphicsDevice.SwapchainFramebuffer;

            if (target != _currentTarget && !target.OutputDescription.Equals(_currentTarget.OutputDescription))
            {
                // TODO graphics pipelines
                throw new NotImplementedException();
            }
            _currentTarget = target;
        }

        /// <summary>
        /// Update the transformation matrix so coordinates match pixel coordinates on the
        /// currently active <see cref="Framebuffer"/>. Call this after changing the render
        /// target with <see cref="SetTarget"/> or when the active <see cref="Framebuffer"/> is resized.
        /// </summary>
        public void UpdateWvp()
        {
            Console.WriteLine($"Update Wvp: ({_currentTarget.Width}, {_currentTarget.Height})");
            UpdateWvp((int) _currentTarget.Width, (int) _currentTarget.Height);
        }

        private void UpdateWvp(int width, int height)
        {
            var wvp = Matrix4x4.CreateOrthographicOffCenter(0, width, height, 0, 0, 1);
            GraphicsDevice.UpdateBuffer(_wvpBuffer, 0, ref wvp);
        }

        private void Grow()
        {
            _freeIds.AddRange(Enumerable.Range(_textures.Length, _textures.Length));
            Array.Resize(ref _textures, _textures.Length * 2);
            Array.Resize(ref _textureViews, _textureViews.Length * 2);
            Array.Resize(ref _textureResourceSets, _textureResourceSets.Length * 2);
        }

        /// <summary>
        /// Register a texture.
        /// </summary>
        /// <param name="path">Path to the texture.</param>
        /// <param name="name">Name of the image. The filename is used when <code>null</code> is passed.</param>
        /// <param name="sampler">
        ///   Sampler to use. Uses <see cref="global::Veldrid.GraphicsDevice.LinearSampler"/>
        ///   when <code>null</code>.
        /// </param>
        /// <returns>The name of the texture.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="path"/> is <code>null</code>.</exception>
        /// <exception cref="ArgumentException">If the file does not exist.</exception>
        public string Register(string path, string name = null, Sampler sampler = null)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path))
                throw new ArgumentException($"File does not exist, '{path}'.", nameof(path));

            var key = name ?? Path.GetFileNameWithoutExtension(path);

            var ist = new ImageSharpTexture(path);
            var tex = ist.CreateDeviceTexture(GraphicsDevice, GraphicsDevice.ResourceFactory);

            return Register(tex, key, sampler);
        }

        /// <summary>
        /// Register a texture-sampler combination.
        /// </summary>
        /// <param name="texture">The texture to register.</param>
        /// <param name="name">Name of the texture.</param>
        /// <param name="sampler">
        ///   Sampler to use. Uses <see cref="global::Veldrid.GraphicsDevice.LinearSampler"/>
        ///   when <code>null</code>.
        /// </param>
        /// <returns><paramref name="name"/>.</returns>
        /// <exception cref="ArgumentNullException">
        ///   If <paramref name="texture"/> or <paramref name="name"/> is <code>null</code>.
        /// </exception>
        /// <exception cref="ArgumentException">If a texture with the same name is already registered.</exception>
        public string Register(Texture texture, string name, Sampler sampler = null)
        {
            if (texture == null)
                throw new ArgumentNullException(nameof(texture));
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            if (_textureIds.ContainsKey(name))
                throw new ArgumentException($"Texture with name '{name}' already registered.", nameof(name));

            if (_freeIds.Count == 0)
                Grow();

            var id = _freeIds[0];
            _freeIds.RemoveAt(0);
            _textureIds[name] = id;
            _textures[id] = texture;
            _textureViews[id] = GraphicsDevice.ResourceFactory.CreateTextureView(texture);
            _textureResourceSets[id] = GraphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                _textureLayout,
                _textureViews[id],
                sampler ?? GraphicsDevice.LinearSampler));

            return name;
        }

        /// <summary>
        /// Release a registered texture. If no texture with the given name is registered, nothing happens.
        /// </summary>
        /// <param name="name">The name of the texture to release.</param>
        public void Release(string name)
        {
            if (_textureIds.TryGetValue(name, out var id))
            {
                _textureIds.Remove(name);
                _textures[id].Dispose();
                _textures[id] = null;
                _textureViews[id].Dispose();
                _textureViews[id] = null;
                _textureResourceSets[id].Dispose();
                _textureResourceSets[id] = null;
                _freeIds.Insert(~_freeIds.BinarySearch(id), id);
            }
        }

        /// <summary>
        /// Get the identifier of the texture with the given name.
        /// </summary>
        /// <param name="name">Name of the texture.</param>
        /// <returns>Identifier of the texture.</returns>
        public int GetTexture(string name)
        {
            return _textureIds[name];
        }

        /// <inheritdoc />
        public Point2 GetTextureSize(int texture)
        {
            var t = _textures[texture];
            return new Point2((int) t.Width, (int) t.Height);
        }

        /// <inheritdoc />
        public Vector2 GetTextSize(string text, int font)
        {
            throw new System.NotImplementedException();
        }

        /// <inheritdoc />
        public Rectangle GetViewport()
        {
            return new Rectangle(0, 0, (int) _currentTarget.Width, (int) _currentTarget.Height);
        }

        /// <inheritdoc />
        public void BeginRender(Vertex[] vertexBuffer, int[] indexBuffer, int vertexCount, int indexCount)
        {
            if (_disposed)
                throw new ObjectDisposedException("Can't use renderer after it has been disposed.");

            GraphicsDevice.UpdateBuffer(_vertexBuffer, 0, ref vertexBuffer[0],
                (uint) (vertexCount * Vertex.SizeInBytes));
            GraphicsDevice.UpdateBuffer(_indexBuffer, 0, ref indexBuffer[0], (uint) (indexCount * sizeof(int)));

            // Begin() must be called before commands can be issued.
            _commandList.Begin();

            _commandList.SetFramebuffer(_currentTarget);
            _commandList.ClearColorTarget(0, RgbaFloat.CornflowerBlue);

            _commandList.SetVertexBuffer(0, _vertexBuffer);
            _commandList.SetIndexBuffer(_indexBuffer, IndexFormat.UInt32);
        }

        /// <inheritdoc />
        public void DrawBatch(GraphicsState state, int startIndex, int indexCount, object batchUserData)
        {
            // TODO sampler state

            if (!_pipelines.TryGetValue(state, out var pipeline))
                pipeline = AddPipeline(state);

            _commandList.SetPipeline(pipeline);

            _commandList.SetGraphicsResourceSet(0, _wvpSet);
            _commandList.SetGraphicsResourceSet(1, _textureResourceSets[state.Texture]);

            // Issue a Draw command for a single instance with 4 indices.
            _commandList.DrawIndexed(
                indexCount: (uint) indexCount,
                instanceCount: 1,
                indexStart: 0,
                vertexOffset: 0,
                instanceStart: 0);
        }

        /// <inheritdoc />
        public void EndRender()
        {
            // End() must be called before commands can be submitted for execution.
            _commandList.End();
            GraphicsDevice.SubmitCommands(_commandList);
        }

        private Pipeline AddPipeline(GraphicsState state)
        {
            var bds = state.BlendState == BlendState.AlphaBlend
                ? BlendStateDescription.SingleAlphaBlend
                : BlendStateDescription.SingleOverrideBlend;
            var gpd = new GraphicsPipelineDescription();
            gpd.BlendState = bds;
            gpd.DepthStencilState = DepthStencilStateDescription.Disabled;
            gpd.RasterizerState = new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid,
                FrontFace.Clockwise, true, state.UseScissorRect);
            gpd.PrimitiveTopology = PrimitiveTopology.TriangleList;
            gpd.ShaderSet = _shaderSet;
            gpd.ResourceLayouts = _resourceLayouts;
            gpd.Outputs = _currentTarget.OutputDescription;
            var pipeline = GraphicsDevice.ResourceFactory.CreateGraphicsPipeline(gpd);
            _pipelines[state] = pipeline;
            return pipeline;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            foreach (var pipeline in _pipelines.Values)
                pipeline.Dispose();
            _textureLayout.Dispose();
            _vertexShader.Dispose();
            _fragmentShader.Dispose();
            _commandList.Dispose();
            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
            GraphicsDevice.Dispose();

            _disposed = true;
        }
    }

    internal static class VeldridHelper
    {
        public static Stream OpenEmbeddedAssetStream(string name, Type t) => t.Assembly.GetManifestResourceStream(name);

        public static Shader LoadShader(ResourceFactory factory, string set, ShaderStages stage, string entryPoint)
        {
            string name = $"{set}-{stage.ToString().ToLower()}.{GetExtension(factory.BackendType)}";
            return factory.CreateShader(new ShaderDescription(stage, ReadEmbeddedAssetBytes(name), entryPoint));
        }

        public static byte[] ReadEmbeddedAssetBytes(string name)
        {
            using (Stream stream = OpenEmbeddedAssetStream(name, typeof(VeldridHelper)))
            {
                byte[] bytes = new byte[stream.Length];
                using (MemoryStream ms = new MemoryStream(bytes))
                {
                    stream.CopyTo(ms);
                    return bytes;
                }
            }
        }

        private static string GetExtension(GraphicsBackend backendType)
        {
            return (backendType == GraphicsBackend.Direct3D11)
                ? "hlsl.bytes"
                : (backendType == GraphicsBackend.Vulkan)
                    ? "450.glsl.spv"
                    : (backendType == GraphicsBackend.Metal)
                        ? "ios.metallib"
                        : (backendType == GraphicsBackend.OpenGL)
                            ? "330.glsl"
                            : "300.glsles";
        }
    }
}