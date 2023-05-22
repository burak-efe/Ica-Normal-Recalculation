using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Serialization;

namespace IcaNormal
{
    [RequireComponent(typeof(Renderer))]
    public class IcaRuntimeNormalSolver : MonoBehaviour
    {
        public enum NormalRecalculateMethodEnum
        {
            Cached,
            Bursted
        }

        public enum NormalOutputEnum
        {
            WriteToMesh,
            WriteToMaterial
        }

        [FormerlySerializedAs("Method")] public NormalRecalculateMethodEnum CalculateMethod = NormalRecalculateMethodEnum.Bursted;
        public NormalOutputEnum NormalOutputTarget = NormalOutputEnum.WriteToMesh;

        [Tooltip("Smoothing angle only usable with bursted method")] [Range(0, 180)] public float SmoothingAngle = 120f;

        public bool RecalculateOnStart;

        public bool CalculateBlendShapes;

        [FormerlySerializedAs("DataCache")] [Tooltip("Data cache asset required when using cached method. You can create this on project tab context menu/plugins /Mesh data cache.")] [SerializeField]
        private IcaMeshDataCache _dataCache;

        [Tooltip("Asset of this model in zero pose. Only necessary when using Calculate Blend Shapes option")] public GameObject ModelPrefab;

        private Renderer _renderer;
        private Mesh _mesh;
        private List<Vector3> _normalsList;
        private List<Vector4> _tangentsList;
        private Vector3[] _normals;
        private Vector4[] _tangents;

        //compute buffer for passing data into shaders
        private ComputeBuffer _normalsOutBuffer;
        private ComputeBuffer _tangentsOutBuffer;
        private bool _isComputeBuffersCreated;

        private Mesh _tempMesh;

        private void Start()
        {
            CacheComponents();

            _normalsList = new List<Vector3>(_mesh.vertexCount);
            _tangentsList = new List<Vector4>(_mesh.vertexCount);
            _mesh.GetNormals(_normalsList);
            _mesh.GetTangents(_tangentsList);
            _normals = _normalsList.ToArray();
            _tangents = _tangentsList.ToArray();
            _tempMesh = new Mesh();

            if (CalculateMethod == NormalRecalculateMethodEnum.Cached)
            {
                if (_dataCache == null)
                {
                    Debug.LogWarning("Cached Normal Calculate Method Needs Data File");
                }
            }

            if (NormalOutputTarget == NormalOutputEnum.WriteToMesh)
            {
                _mesh.MarkDynamic();
            }
            else if (NormalOutputTarget == NormalOutputEnum.WriteToMaterial)
            {
                _normalsOutBuffer = new ComputeBuffer(_mesh.vertexCount, sizeof(float) * 3);
                _tangentsOutBuffer = new ComputeBuffer(_mesh.vertexCount, sizeof(float) * 4);
                _isComputeBuffersCreated = true;

                //duplicate all materials here
                for (int i = 0; i < _renderer.materials.Length; i++)
                {
                    _renderer.materials[i] = new Material(_renderer.materials[i]);

                    _renderer.materials[i].SetBuffer("normalsOutBuffer", _normalsOutBuffer);
                    _renderer.materials[i].SetFloat("_Initialized", 1);
                }

                _normalsOutBuffer.SetData(_normals);
                _tangentsOutBuffer.SetData(_tangents);
            }

            if (RecalculateOnStart)
            {
                RecalculateNormals();
            }
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

        private void OnDestroy()
        {
            //Compute buffers need to be destroyed
            if (_isComputeBuffersCreated)
            {
                _normalsOutBuffer.Release();
                _tangentsOutBuffer.Release();
            }
        }

        [ContextMenu("RecalculateNormals")]
        public void RecalculateNormals()
        {
            if (CalculateMethod == NormalRecalculateMethodEnum.Bursted)
            {
                RecalculateBursted();
            }
            else if (CalculateMethod == NormalRecalculateMethodEnum.Cached)
            {
                RecalculateCached();
            }
        }

        private void RecalculateBursted()
        {
            if (CalculateBlendShapes && _renderer is SkinnedMeshRenderer smr)
            {
                var tempObj = Instantiate(ModelPrefab);
                var tempSmr = tempObj.GetComponentInChildren<SkinnedMeshRenderer>();

                tempSmr.sharedMesh = _mesh;
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    tempSmr.SetBlendShapeWeight(i, smr.GetBlendShapeWeight(i));
                }

                tempSmr.BakeMesh(_tempMesh);

                IcaNormalSolverUtils.CalculateNormalDataBursted(_tempMesh, SmoothingAngle, ref _normals, ref _tangents);
                Destroy(tempObj);
            }
            else
            {
                IcaNormalSolverUtils.CalculateNormalDataBursted(_mesh, SmoothingAngle, ref _normals, ref _tangents);
            }

            if (NormalOutputTarget == NormalOutputEnum.WriteToMesh)
            {
                _mesh.SetNormals(_normals);
                _mesh.SetTangents(_tangents);
            }
            else if (NormalOutputTarget == NormalOutputEnum.WriteToMaterial)
            {
                _normalsOutBuffer.SetData(_normals);
                _tangentsOutBuffer.SetData(_tangents);
            }
        }

        private void RecalculateCached()
        {
            var mapCount = _dataCache.DuplicatesData.Count;

            if (mapCount == 0)
            {
                Debug.Log("Mesh Data of " + _mesh.name + " not found. Please do not forget the cache data on contex menu or on start method before recalculating normals");
                return;
            }


            if (CalculateBlendShapes && _renderer is SkinnedMeshRenderer smr)
            {
                var tempObj = Instantiate(ModelPrefab);
                var tempSmr = tempObj.GetComponentInChildren<SkinnedMeshRenderer>();
                tempSmr.sharedMesh = _mesh;
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    tempSmr.SetBlendShapeWeight(i, smr.GetBlendShapeWeight(i));
                }

                tempSmr.BakeMesh(_tempMesh);
                _tempMesh.RecalculateNormals();
                _tempMesh.GetNormals(_normalsList);
                _tempMesh.RecalculateTangents();
                _tempMesh.GetTangents(_tangentsList);
                Destroy(tempObj);
            }
            else
            {
                _tempMesh = _mesh;
                _tempMesh.RecalculateNormals();
                _tempMesh.GetNormals(_normalsList);
                _tempMesh.RecalculateTangents();
                _tempMesh.GetTangents(_tangentsList);
            }


            for (int listIndex = 0; listIndex < mapCount; listIndex++)
            {
                Vector3 normalSum = Vector3.zero;
                Vector4 tangentSum = Vector4.zero;
                var length = _dataCache.DuplicatesData[listIndex].DuplicateIndexes.Length;
                for (int i = 0; i < length; i++)
                {
                    normalSum += _normalsList[_dataCache.DuplicatesData[listIndex].DuplicateIndexes[i]];
                    tangentSum += _tangents[_dataCache.DuplicatesData[listIndex].DuplicateIndexes[i]];
                }

                normalSum = normalSum.normalized;
                Vector3 tangXYZ = new Vector3(tangentSum.x, tangentSum.y, tangentSum.z);
                tangXYZ = tangXYZ.normalized;
                tangentSum = new Vector4(tangXYZ.normalized.x, tangXYZ.y, tangXYZ.z, Mathf.Clamp(tangentSum.w, -1f, 1f));
                for (int i = 0; i < length; i++)
                {
                    _normalsList[_dataCache.DuplicatesData[listIndex].DuplicateIndexes[i]] = normalSum;
                    _tangentsList[_dataCache.DuplicatesData[listIndex].DuplicateIndexes[i]] = tangentSum;
                }
            }

            if (NormalOutputTarget == NormalOutputEnum.WriteToMesh)
            {
                _mesh.SetNormals(_normalsList);
                _mesh.SetTangents(_tangentsList);
            }
            else if (NormalOutputTarget == NormalOutputEnum.WriteToMaterial)
            {
                _normalsOutBuffer.SetData(_normalsList);
                _tangentsOutBuffer.SetData(_tangentsList);
            }
        }
    }
}