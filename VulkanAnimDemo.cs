using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Assimp;

namespace RockGenerationDemo
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SkinnedVertex : IVertexType
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TextureCoordinate;
        public Vector4 BlendIndices;
        public Vector4 BlendWeights;

        public static readonly VertexDeclaration VertexDeclaration = new VertexDeclaration(
    new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
    new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
    new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
    // Restore native Vulkan skinning semantics
    new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.BlendIndices, 0),
    new VertexElement(48, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0)
);

        VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
    }

    public class RigNode
    {
        public string Name;
        public Microsoft.Xna.Framework.Matrix BaseLocalTransform;
        public Microsoft.Xna.Framework.Matrix CurrentLocalTransform;
        public Microsoft.Xna.Framework.Matrix GlobalTransform;
        public RigNode Parent;
        public List<RigNode> Children = new List<RigNode>();
    }

    public class VulkanAnimDemo : Game
    {
        private GraphicsDeviceManager _graphics;
        private Effect _skinningEffect;

        private VertexBuffer _vertexBuffer;
        private IndexBuffer _indexBuffer;

        private RigNode _rootNode;
        private readonly List<Microsoft.Xna.Framework.Matrix> _boneOffsetMatrices = new List<Microsoft.Xna.Framework.Matrix>();
        private readonly Dictionary<string, int> _boneNameToIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, RigNode> _nodeLookup = new Dictionary<string, RigNode>();
        private readonly Dictionary<int, RigNode> _boneIndexToNode = new Dictionary<int, RigNode>();
        private RigNode _spineNode;
        private RigNode _armNode;

        private Vector3[] _bindPositions;
        private Vector3[] _bindNormals;
        private Vector4[] _bindBlendIndices;
        private Vector4[] _bindBlendWeights;
        private int _sampleSpineVertexIndex = -1;

        private bool _cpuVerifyMode = false;
        private DynamicVertexBuffer _cpuVerifyVertexBuffer;
        private BasicEffect _cpuVerifyEffect;
        private VertexPositionColor[] _cpuVerifyVertices;

        private int _totalVertexCount;
        private int _weightedVertexCount;
        private float _titleUpdateTimer;
        private Vector3 _meshBoundsMin, _meshBoundsMax;

        private Microsoft.Xna.Framework.Matrix _world, _view, _projection;
        private Microsoft.Xna.Framework.Matrix[] _shaderBones;
        private Vector3 _cameraTarget = Vector3.Zero;

        private float _modelPitch = 0f;
        private float _modelYaw = 0f;
        private MouseState _previousMouseState;
        private KeyboardState _previousKeyboardState;
        private float _cameraDistance = 10f;
        private Vector3 _modelPosition = Vector3.Zero;

        public VulkanAnimDemo()
        {
            _graphics = new GraphicsDeviceManager(this);
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
        }

        protected override void Initialize()
        {
            _world = Microsoft.Xna.Framework.Matrix.Identity;
            _view = Microsoft.Xna.Framework.Matrix.CreateLookAt(new Vector3(0, 5, 10), new Vector3(0, 5, 0), Vector3.Up);
            _projection = Microsoft.Xna.Framework.Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, GraphicsDevice.Viewport.AspectRatio, 0.1f, 1000f);
            base.Initialize();
        }

        protected override void LoadContent()
        {
            _skinningEffect = Content.Load<Effect>("Characters/Animation_Dummy/CustomSkinnedEffect");
            LoadModelViaAssimp("Content/Characters/Animation_Dummy/msm.fbx");

            _cpuVerifyEffect = new BasicEffect(GraphicsDevice)
            {
                VertexColorEnabled = true,
                LightingEnabled = false
            };
        }

        private void LoadModelViaAssimp(string filePath)
        {
            var importer = new AssimpContext();
            var scene = importer.ImportFile(filePath, PostProcessSteps.Triangulate | PostProcessSteps.FlipUVs | PostProcessSteps.CalculateTangentSpace);

            var mesh = scene.Meshes.FirstOrDefault(m => m.HasBones);
            if (mesh == null) throw new Exception("CRITICAL: No rigged meshes found in this FBX.");

            List<Vector2>[] vertexWeights = new List<Vector2>[mesh.VertexCount];
            for (int i = 0; i < mesh.VertexCount; i++) vertexWeights[i] = new List<Vector2>();

            for (int b = 0; b < mesh.Bones.Count; b++)
            {
                var bone = mesh.Bones[b];
                _boneNameToIndex[bone.Name] = b;

                // Original transposed mapping required for XNA row-major translation alignment
                _boneOffsetMatrices.Add(ToXnaMatrix(bone.OffsetMatrix));

                foreach (var weight in bone.VertexWeights)
                {
                    vertexWeights[weight.VertexID].Add(new Vector2(b, weight.Weight));
                }
            }

            _shaderBones = new Microsoft.Xna.Framework.Matrix[72];
            for (int i = 0; i < 72; i++) _shaderBones[i] = Microsoft.Xna.Framework.Matrix.Identity;

            Vector3 boundsMin = new Vector3(float.MaxValue);
            Vector3 boundsMax = new Vector3(float.MinValue);

            SkinnedVertex[] vertices = new SkinnedVertex[mesh.VertexCount];
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var vw = vertexWeights[i].OrderByDescending(w => w.Y).Take(4).ToList();

                Vector4 indices = Vector4.Zero;
                Vector4 weights = Vector4.Zero;
                float sum = 0f;

                for (int j = 0; j < vw.Count; j++)
                {
                    if (j == 0) { indices.X = vw[j].X; weights.X = vw[j].Y; }
                    if (j == 1) { indices.Y = vw[j].X; weights.Y = vw[j].Y; }
                    if (j == 2) { indices.Z = vw[j].X; weights.Z = vw[j].Y; }
                    if (j == 3) { indices.W = vw[j].X; weights.W = vw[j].Y; }
                    sum += vw[j].Y;
                }

                if (sum > 0) weights /= sum;

                var pos = new Vector3(mesh.Vertices[i].X, mesh.Vertices[i].Y, mesh.Vertices[i].Z);
                boundsMin = Vector3.Min(boundsMin, pos);
                boundsMax = Vector3.Max(boundsMax, pos);

                vertices[i] = new SkinnedVertex
                {
                    Position = pos,
                    Normal = new Vector3(mesh.Normals[i].X, mesh.Normals[i].Y, mesh.Normals[i].Z),
                    TextureCoordinate = mesh.HasTextureCoords(0) ? new Vector2(mesh.TextureCoordinateChannels[0][i].X, mesh.TextureCoordinateChannels[0][i].Y) : Vector2.Zero,
                    BlendIndices = indices,
                    BlendWeights = weights
                };
            }

            _meshBoundsMin = boundsMin;
            _meshBoundsMax = boundsMax;
            _totalVertexCount = vertices.Length;
            _weightedVertexCount = vertices.Count(v => (v.BlendWeights.X + v.BlendWeights.Y + v.BlendWeights.Z + v.BlendWeights.W) > 0.0001f);

            _bindPositions = vertices.Select(v => v.Position).ToArray();
            _bindNormals = vertices.Select(v => v.Normal).ToArray();
            _bindBlendIndices = vertices.Select(v => v.BlendIndices).ToArray();
            _bindBlendWeights = vertices.Select(v => v.BlendWeights).ToArray();
            _cpuVerifyVertices = new VertexPositionColor[vertices.Length];

            _vertexBuffer = new VertexBuffer(GraphicsDevice, SkinnedVertex.VertexDeclaration, vertices.Length, BufferUsage.WriteOnly);
            _vertexBuffer.SetData(vertices);

            int[] indicesArray = mesh.GetIndices();
            _indexBuffer = new IndexBuffer(GraphicsDevice, IndexElementSize.ThirtyTwoBits, indicesArray.Length, BufferUsage.WriteOnly);
            _indexBuffer.SetData(indicesArray);

            _cpuVerifyVertexBuffer = new DynamicVertexBuffer(GraphicsDevice, VertexPositionColor.VertexDeclaration, vertices.Length, BufferUsage.WriteOnly);

            _rootNode = BuildHierarchy(scene.RootNode, null);

            foreach (var kvp in _boneNameToIndex)
            {
                string cleanName = kvp.Key.Replace("mixamorig:", "").Replace("Armature_", "").Trim();
                RigNode node = _nodeLookup.Values.FirstOrDefault(n => n.Name.Replace("mixamorig:", "").Replace("Armature_", "").Trim().Equals(cleanName, StringComparison.OrdinalIgnoreCase));
                if (node != null) _boneIndexToNode[kvp.Value] = node;
            }

            _spineNode = _nodeLookup.Values.FirstOrDefault(n => n.Name.Replace("mixamorig:", "").Replace("Armature_", "").Trim().Equals("Spine", StringComparison.OrdinalIgnoreCase));
            _armNode = _nodeLookup.Values.FirstOrDefault(n => n.Name.Replace("mixamorig:", "").Replace("Armature_", "").Trim().Equals("upperArm.r", StringComparison.OrdinalIgnoreCase));

            if (_boneNameToIndex.TryGetValue("Spine", out int spineBoneIdx))
            {
                float bestWeight = -1f;
                for (int i = 0; i < _bindBlendIndices.Length; i++)
                {
                    Vector4 idx = _bindBlendIndices[i];
                    Vector4 w = _bindBlendWeights[i];
                    float wForSpine = 0f;
                    if ((int)idx.X == spineBoneIdx) wForSpine = Math.Max(wForSpine, w.X);
                    if ((int)idx.Y == spineBoneIdx) wForSpine = Math.Max(wForSpine, w.Y);
                    if ((int)idx.Z == spineBoneIdx) wForSpine = Math.Max(wForSpine, w.Z);
                    if ((int)idx.W == spineBoneIdx) wForSpine = Math.Max(wForSpine, w.W);

                    if (wForSpine > bestWeight)
                    {
                        bestWeight = wForSpine;
                        _sampleSpineVertexIndex = i;
                    }
                }
            }

            Vector3 rawSize = _meshBoundsMax - _meshBoundsMin;
            Vector3 rawCenter = (_meshBoundsMin + _meshBoundsMax) * 0.5f;
            float maxDimension = Math.Max(rawSize.X, Math.Max(rawSize.Y, rawSize.Z));
            if (maxDimension < 0.0001f) maxDimension = 1f;

            Microsoft.Xna.Framework.Matrix axisFix = Microsoft.Xna.Framework.Matrix.CreateRotationX(-MathHelper.PiOver2);
            _cameraTarget = Vector3.Transform(rawCenter, axisFix);
            _cameraDistance = maxDimension * 2.5f;
            float nearPlane = Math.Max(0.01f, maxDimension * 0.001f);
            float farPlane = maxDimension * 50f;

            _view = Microsoft.Xna.Framework.Matrix.CreateLookAt(_cameraTarget + new Vector3(0, 0, _cameraDistance), _cameraTarget, Vector3.Up);
            _projection = Microsoft.Xna.Framework.Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, GraphicsDevice.Viewport.AspectRatio, nearPlane, farPlane);
        }

        private RigNode BuildHierarchy(Node assimpNode, RigNode parent)
        {
            var rigNode = new RigNode
            {
                Name = assimpNode.Name,
                BaseLocalTransform = ToXnaMatrix(assimpNode.Transform),
                Parent = parent
            };
            rigNode.CurrentLocalTransform = rigNode.BaseLocalTransform;
            _nodeLookup[rigNode.Name] = rigNode;
            foreach (var child in assimpNode.Children) rigNode.Children.Add(BuildHierarchy(child, rigNode));
            return rigNode;
        }

        protected override void Update(GameTime gameTime)
        {
            float time = (float)gameTime.TotalGameTime.TotalSeconds;
            float deltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;

            KeyboardState kstate = Keyboard.GetState();
            float rotationSpeed = 10.5f;

            if (kstate.IsKeyDown(Keys.Left)) _modelYaw += rotationSpeed * deltaTime;
            if (kstate.IsKeyDown(Keys.Right)) _modelYaw -= rotationSpeed * deltaTime;
            if (kstate.IsKeyDown(Keys.Up)) _modelPitch += rotationSpeed * deltaTime;
            if (kstate.IsKeyDown(Keys.Down)) _modelPitch -= rotationSpeed * deltaTime;

            if (kstate.IsKeyDown(Keys.V) && !_previousKeyboardState.IsKeyDown(Keys.V)) _cpuVerifyMode = !_cpuVerifyMode;

            MouseState mstate = Mouse.GetState();
            int scrollDelta = mstate.ScrollWheelValue - _previousMouseState.ScrollWheelValue;
            if (scrollDelta != 0)
            {
                _cameraDistance -= scrollDelta * 0.01f;
                _cameraDistance = MathHelper.Clamp(_cameraDistance, 0.01f, 5000f);
                _view = Microsoft.Xna.Framework.Matrix.CreateLookAt(_cameraTarget + new Vector3(0, 0, _cameraDistance), _cameraTarget, Vector3.Up);
            }

            if (mstate.LeftButton == ButtonState.Pressed)
            {
                _modelYaw -= (mstate.X - _previousMouseState.X) * 0.01f;
                _modelPitch -= (mstate.Y - _previousMouseState.Y) * 0.01f;
            }

            if (mstate.RightButton == ButtonState.Pressed)
            {
                _modelPosition.X += (mstate.X - _previousMouseState.X) * 0.01f;
                _modelPosition.Y -= (mstate.Y - _previousMouseState.Y) * 0.01f;
            }

            _previousMouseState = mstate;
            _previousKeyboardState = kstate;

            _world = Microsoft.Xna.Framework.Matrix.CreateRotationX(-MathHelper.PiOver2) *
                     Microsoft.Xna.Framework.Matrix.CreateRotationY(_modelYaw) *
                     Microsoft.Xna.Framework.Matrix.CreateRotationX(_modelPitch) *
                     Microsoft.Xna.Framework.Matrix.CreateTranslation(_modelPosition);

            ResetTransforms(_rootNode);

            int axisPhase = ((int)(time / 3.0f)) % 3;
            float bendAngle = (float)Math.Sin(time * 1.5f) * 1.2f;
            float armSwingAngle = (float)Math.Sin(time * 0.8f) * 1.4f;

            Microsoft.Xna.Framework.Matrix bendRotation;
            Microsoft.Xna.Framework.Matrix armRotation;
            string axisLabel;

            switch (axisPhase)
            {
                case 0:
                    bendRotation = Microsoft.Xna.Framework.Matrix.CreateRotationX(bendAngle);
                    armRotation = Microsoft.Xna.Framework.Matrix.CreateRotationX(armSwingAngle);
                    axisLabel = "X";
                    break;
                case 1:
                    bendRotation = Microsoft.Xna.Framework.Matrix.CreateRotationY(bendAngle);
                    armRotation = Microsoft.Xna.Framework.Matrix.CreateRotationY(armSwingAngle);
                    axisLabel = "Y";
                    break;
                default:
                    bendRotation = Microsoft.Xna.Framework.Matrix.CreateRotationZ(bendAngle);
                    armRotation = Microsoft.Xna.Framework.Matrix.CreateRotationZ(armSwingAngle);
                    axisLabel = "Z";
                    break;
            }

            // Pure rotation multiplication restores structural integrity.
            if (_spineNode != null)
            {
                _spineNode.CurrentLocalTransform = bendRotation * _spineNode.BaseLocalTransform;
            }

            if (_armNode != null)
            {
                _armNode.CurrentLocalTransform = armRotation * _armNode.BaseLocalTransform;
            }

            CalculateGlobalTransforms(_rootNode, Microsoft.Xna.Framework.Matrix.Identity);

            foreach (var kvp in _boneIndexToNode)
            {
                int boneIndex = kvp.Key;
                RigNode node = kvp.Value;
                _shaderBones[boneIndex] = _boneOffsetMatrices[boneIndex] * node.GlobalTransform;
            }

            Vector3 samplePosWorld = Vector3.Zero;
            if (_sampleSpineVertexIndex >= 0)
            {
                Vector4 idx = _bindBlendIndices[_sampleSpineVertexIndex];
                Vector4 w = _bindBlendWeights[_sampleSpineVertexIndex];
                Microsoft.Xna.Framework.Matrix skin =
                    _shaderBones[(int)idx.X] * w.X +
                    _shaderBones[(int)idx.Y] * w.Y +
                    _shaderBones[(int)idx.Z] * w.Z +
                    _shaderBones[(int)idx.W] * w.W;
                samplePosWorld = Vector3.Transform(_bindPositions[_sampleSpineVertexIndex], skin);
            }

            if (_cpuVerifyMode) UpdateCpuVerifyMesh();

            _titleUpdateTimer += deltaTime;
            if (_titleUpdateTimer >= 0.1f)
            {
                _titleUpdateTimer = 0f;
                Window.Title = $"Spine:{(_spineNode != null ? "OK" : "MISSING")} Arm:{(_armNode != null ? "OK" : "MISSING")} | " +
                               $"Bones: {_boneIndexToNode.Count}/{_boneNameToIndex.Count} | " +
                               $"Weighted: {_weightedVertexCount}/{_totalVertexCount} | " +
                               $"Mode: {(_cpuVerifyMode ? "CPU(V)" : "GPU")} | " +
                               $"Axis: {axisLabel} {bendAngle:F2}rad | " +
                               $"SampleVtx: ({samplePosWorld.X:F2},{samplePosWorld.Y:F2},{samplePosWorld.Z:F2}) | " +
                               $"CamDist: {_cameraDistance:F2}";
            }

            base.Update(gameTime);
        }

        private void UpdateCpuVerifyMesh()
        {
            Vector3 lightDirection = Vector3.Normalize(new Vector3(-1.0f, -1.0f, 0.5f));
            Vector3 baseColor = new Vector3(0.7f, 0.7f, 0.7f);

            for (int i = 0; i < _bindPositions.Length; i++)
            {
                Vector4 idx = _bindBlendIndices[i];
                Vector4 w = _bindBlendWeights[i];
                float weightSum = w.X + w.Y + w.Z + w.W;

                Vector3 skinnedPos;
                Vector3 skinnedNormal;

                if (weightSum > 0.001f)
                {
                    Microsoft.Xna.Framework.Matrix skin =
                        _shaderBones[(int)idx.X] * w.X +
                        _shaderBones[(int)idx.Y] * w.Y +
                        _shaderBones[(int)idx.Z] * w.Z +
                        _shaderBones[(int)idx.W] * w.W;

                    skinnedPos = Vector3.Transform(_bindPositions[i], skin);
                    skinnedNormal = Vector3.Normalize(Vector3.TransformNormal(_bindNormals[i], skin));
                }
                else
                {
                    skinnedPos = _bindPositions[i];
                    skinnedNormal = _bindNormals[i];
                }

                float lightIntensity = Math.Max(0.25f, Vector3.Dot(skinnedNormal, -lightDirection));
                Vector3 lit = baseColor * lightIntensity;
                _cpuVerifyVertices[i] = new VertexPositionColor(skinnedPos, new Color(lit.X, lit.Y, lit.Z));
            }
            _cpuVerifyVertexBuffer.SetData(_cpuVerifyVertices, 0, _cpuVerifyVertices.Length, SetDataOptions.Discard);
        }

        private void ResetTransforms(RigNode node)
        {
            node.CurrentLocalTransform = node.BaseLocalTransform;
            foreach (var child in node.Children) ResetTransforms(child);
        }

        private void CalculateGlobalTransforms(RigNode node, Microsoft.Xna.Framework.Matrix parentGlobal)
        {
            node.GlobalTransform = node.CurrentLocalTransform * parentGlobal;
            foreach (var child in node.Children) CalculateGlobalTransforms(child, node.GlobalTransform);
        }

        protected override void Draw(GameTime gameTime)
        {
            GraphicsDevice.Clear(Color.CornflowerBlue);
            GraphicsDevice.RasterizerState = RasterizerState.CullClockwise;
            GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            GraphicsDevice.Indices = _indexBuffer;

            if (_cpuVerifyMode)
            {
                _cpuVerifyEffect.World = _world;
                _cpuVerifyEffect.View = _view;
                _cpuVerifyEffect.Projection = _projection;

                GraphicsDevice.SetVertexBuffer(_cpuVerifyVertexBuffer);
                foreach (EffectPass pass in _cpuVerifyEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    GraphicsDevice.DrawIndexedPrimitives(Microsoft.Xna.Framework.Graphics.PrimitiveType.TriangleList, 0, 0, _indexBuffer.IndexCount / 3);
                }
            }
            else
            {
                GraphicsDevice.SetVertexBuffer(_vertexBuffer);
                _skinningEffect.Parameters["World"].SetValue(_world);
                _skinningEffect.Parameters["View"].SetValue(_view);
                _skinningEffect.Parameters["Projection"].SetValue(_projection);
                _skinningEffect.Parameters["Bones"].SetValue(_shaderBones);
                //_skinningEffect.Parameters["Time"].SetValue((float)gameTime.TotalGameTime.TotalSeconds);

                foreach (EffectPass pass in _skinningEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    GraphicsDevice.DrawIndexedPrimitives(Microsoft.Xna.Framework.Graphics.PrimitiveType.TriangleList, 0, 0, _indexBuffer.IndexCount / 3);
                }
            }
            base.Draw(gameTime);
        }

        private Microsoft.Xna.Framework.Matrix ToXnaMatrix(Assimp.Matrix4x4 m)
        {
            return new Microsoft.Xna.Framework.Matrix(
                m.A1, m.B1, m.C1, m.D1,
                m.A2, m.B2, m.C2, m.D2,
                m.A3, m.B3, m.C3, m.D3,
                m.A4, m.B4, m.C4, m.D4
            );
        }
    }
}