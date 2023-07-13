using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace IcaNormal
{
    [RequireComponent(typeof(Renderer))]
    public class RuntimeNormalSolver : MonoBehaviour
    {
        public enum NormalRecalculateMethodEnum
        {
            SDBursted,
            CachedParallel
        }

        public enum NormalOutputEnum
        {
            WriteToMesh,
            WriteToMaterial
        }

        public NormalRecalculateMethodEnum CalculateMethod = NormalRecalculateMethodEnum.SDBursted;
        public NormalOutputEnum NormalOutputTarget = NormalOutputEnum.WriteToMesh;

        [Range(0, 180)]
        [Tooltip("Smoothing angle only usable with bursted method")]
        public float SmoothingAngle = 120f;

        public bool RecalculateOnStart;
        public bool RecalculateTangents;
        public bool CalculateBlendShapes;

        [SerializeField]
        [Tooltip("Data cache asset required when using cached method. You can create this on project tab context menu/plugins /Mesh data cache.")]
        private MeshDataCache _dataCache;

        [Tooltip("Asset of this model in zero pose. Only necessary when using Calculate Blend Shapes option")]
        public GameObject ModelPrefab;

        private Renderer _renderer;
        private Mesh _mesh;
        private Mesh _tempMesh;
        private GameObject _tempObj;
        private SkinnedMeshRenderer _tempSmr;

        private NativeArray<int> _nativeAdjacencyList;
        private NativeArray<int2> _nativeAdjacencyMap;
        private NativeArray<int> _indices;
        private NativeArray<float3> _vertices;
        private NativeArray<float3> _normals;
        private NativeArray<float4> _tangents;

        private NativeArray<float2> _uvs;

        private Mesh.MeshDataArray _meshDataArray;
        private Mesh.MeshData _mainMeshData;

        //compute buffer for passing data into shaders
        private ComputeBuffer _normalsOutBuffer;
        private ComputeBuffer _tangentsOutBuffer;
        private bool _isComputeBuffersCreated;

        private void Start()
        {
            CacheComponents();
            InitNativeContainers();

            if (NormalOutputTarget == NormalOutputEnum.WriteToMesh)
            {
                _mesh.MarkDynamic();
            }
            else if (NormalOutputTarget == NormalOutputEnum.WriteToMaterial)
            {
                _normalsOutBuffer = new ComputeBuffer(_mesh.vertexCount, sizeof(float) * 3);
                _tangentsOutBuffer = new ComputeBuffer(_mesh.vertexCount, sizeof(float) * 4);
                _isComputeBuffersCreated = true;

                //duplicate all materials
                for (int i = 0; i < _renderer.materials.Length; i++)
                {
                    _renderer.materials[i] = new Material(_renderer.materials[i]);
                    _renderer.materials[i].SetBuffer("normalsOutBuffer", _normalsOutBuffer);
                    _renderer.materials[i].SetBuffer("tangentsOutBuffer", _tangentsOutBuffer);
                    _renderer.materials[i].SetFloat("_Initialized", 1);
                }

                _normalsOutBuffer.SetData(_normals);
                _tangentsOutBuffer.SetData(_tangents);
            }

            if (CalculateBlendShapes && _renderer is SkinnedMeshRenderer)
            {
                _tempObj = Instantiate(ModelPrefab, transform);
                _tempSmr = GetComponentInChildren<SkinnedMeshRenderer>();
                _tempObj.SetActive(false);
            }

            if (RecalculateOnStart)
                RecalculateNormals();
        }

        private void CacheComponents()
        {
            _renderer = GetComponent<Renderer>();

            if (_renderer is SkinnedMeshRenderer smr)
            {
                _mesh = smr.sharedMesh;
            }
            else if (_renderer is MeshRenderer)
            {
                _mesh = GetComponent<MeshFilter>().sharedMesh;
            }
        }

        private void InitNativeContainers()
        {
            _tempMesh = new Mesh();
            _meshDataArray = Mesh.AcquireReadOnlyMeshData(_mesh);
            _mainMeshData = _meshDataArray[0];
            _vertices = new NativeArray<float3>(_mesh.vertexCount, Allocator.Persistent);
            _normals = new NativeArray<float3>(_mesh.vertexCount, Allocator.Persistent);
            _tangents = new NativeArray<float4>(_mesh.vertexCount, Allocator.Persistent);
            _uvs = new NativeArray<float2>(_mesh.vertexCount, Allocator.Persistent);
            _mainMeshData.GetNormals(_normals.Reinterpret<Vector3>());
            _mainMeshData.GetTangents(_tangents.Reinterpret<Vector4>());
            _mainMeshData.GetUVs(0, _uvs.Reinterpret<Vector2>());

            if (CalculateMethod == NormalRecalculateMethodEnum.CachedParallel)
            {
                if (_dataCache != null)
                {
                    Profiler.BeginSample("GetCacheDataFromAsset");
                    _nativeAdjacencyList = new NativeArray<int>(_dataCache.SerializedAdjacencyList, Allocator.Persistent);
                    _nativeAdjacencyMap = new NativeArray<int2>(_dataCache.SerializedAdjacencyMapper, Allocator.Persistent);
                    _indices = new NativeArray<int>(_dataCache.SerializedIndices, Allocator.Persistent);
                    Profiler.EndSample();
                }
                else
                {
                    CalculateCache();
                }
            }
        }

        private void OnDestroy()
        {
            //Compute buffers need to be destroyed
            if (_isComputeBuffersCreated)
            {
                _normalsOutBuffer.Release();
                _tangentsOutBuffer.Release();
            }

            _vertices.Dispose();
            _normals.Dispose();
            _tangents.Dispose();
            _meshDataArray.Dispose();
            _uvs.Dispose();

            if (CalculateMethod == NormalRecalculateMethodEnum.CachedParallel)
            {
                _nativeAdjacencyList.Dispose();
                _nativeAdjacencyMap.Dispose();
                _indices.Dispose();
            }
        }

        [ContextMenu("RecalculateNormals")]
        public void RecalculateNormals()
        {
            if (CalculateMethod == NormalRecalculateMethodEnum.SDBursted)
                RecalculateSDBursted();
            else if (CalculateMethod == NormalRecalculateMethodEnum.CachedParallel)
                RecalculateCachedParallel();
        }

        public void CalculateCache()
        {
            Profiler.BeginSample("CalculateCacheData");

            Profiler.BeginSample("GetIndices");
            _mainMeshData.GetAllIndices(out _indices, Allocator.Persistent);
            Profiler.EndSample();

            Profiler.BeginSample("GetVertices");
            var tempVertices = new NativeArray<float3>(_mainMeshData.vertexCount, Allocator.Temp);
            _mainMeshData.GetVertices(tempVertices.Reinterpret<Vector3>());
            Profiler.EndSample();

            Profiler.BeginSample("GetVertexPosHashMap");
            VertexPositionMapper.GetVertexPosHashMap(in tempVertices, out var tempPosGraph, Allocator.Temp);
            Profiler.EndSample();

            Profiler.BeginSample("GetAdjacency");
            AdjacencyMapper.CalculateAdjacencyData(in tempVertices, in _indices, in tempPosGraph, out _nativeAdjacencyList, out _nativeAdjacencyMap, Allocator.Persistent);
            Profiler.EndSample();

            Profiler.EndSample();
        }

        private void RecalculateSDBursted()
        {
            if (CalculateBlendShapes && _renderer is SkinnedMeshRenderer smr)
            {
                SmrUtils.CopyBlendShapes(smr, _tempSmr);
                _tempSmr.BakeMesh(_tempMesh);
                var mda = Mesh.AcquireReadOnlyMeshData(_tempMesh);
                SDBurstedMethod.CalculateNormalData(mda[0], SmoothingAngle, ref _normals, ref _tangents);
                mda.Dispose();
            }
            else
            {
                SDBurstedMethod.CalculateNormalData(_mainMeshData, SmoothingAngle, ref _normals, ref _tangents);
            }

            SetNormals(_normals);
            SetTangents(_tangents);
        }

        private void RecalculateCachedParallel()
        {
            UpdateNativeVertices();
            CachedParallelMethod.CalculateNormalData(_vertices, _indices, ref _normals, _nativeAdjacencyList, _nativeAdjacencyMap);
            SetNormals(_normals);
            
            if (RecalculateTangents)
            {
                CachedParallelMethod.CalculateTangentData(_vertices, _normals, _indices, _uvs, _nativeAdjacencyList, _nativeAdjacencyMap, ref _tangents);
                
                SetTangents(_tangents);
            }
        }

        private void SetNormals(NativeArray<float3> normals)
        {
            if (NormalOutputTarget == NormalOutputEnum.WriteToMesh)
            {
                Profiler.BeginSample("WriteToMesh");
                _mesh.SetNormals(normals);
                Profiler.EndSample();
            }
            else if (NormalOutputTarget == NormalOutputEnum.WriteToMaterial)
            {
                Profiler.BeginSample("WriteToMaterial");
                _normalsOutBuffer.SetData(normals);
                Profiler.EndSample();
            }
        }

        private void SetTangents(NativeArray<float4> tangents)
        {
            if (NormalOutputTarget == NormalOutputEnum.WriteToMesh)
            {
                Profiler.BeginSample("WriteToMesh");
                _mesh.SetTangents(tangents);
                Profiler.EndSample();
            }
            else if (NormalOutputTarget == NormalOutputEnum.WriteToMaterial)
            {
                Profiler.BeginSample("WriteToMaterial");
                _tangentsOutBuffer.SetData(tangents);
                Profiler.EndSample();
            }
        }

        public void TangentsOnlyTest()
        {
            UpdateNativeVerticesFromMeshData(_mainMeshData);
            CachedParallelMethod.CalculateTangentData(_vertices, _normals, _indices, _uvs, _nativeAdjacencyList, _nativeAdjacencyMap, ref _tangents);
            SetTangents(_tangents);
        }

        private void UpdateNativeVertices()
        {
            if (CalculateBlendShapes && _renderer is SkinnedMeshRenderer smr)
            {
                Profiler.BeginSample("GetSMRData");
                SmrUtils.CopyBlendShapes(smr, _tempSmr);
                _tempSmr.BakeMesh(_tempMesh);
                var mda = Mesh.AcquireReadOnlyMeshData(_tempMesh);
                UpdateNativeVerticesFromMeshData(mda[0]);
                mda.Dispose();
                Profiler.EndSample();
            }
            else
            {
                UpdateNativeVerticesFromMeshData(_mainMeshData);
            }
        }

        private void UpdateNativeVerticesFromMeshData(Mesh.MeshData data)
        {
            data.GetVertices(_vertices.Reinterpret<Vector3>());
        }
    }
}