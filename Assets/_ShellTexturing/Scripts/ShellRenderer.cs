using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Runtime.InteropServices;
using System.IO;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class ShellRenderer : MonoBehaviour
{
    [SerializeField] ComputeShader mShellCompute;
    [SerializeField] Material mShellRenderMaterial;

    [Header("Shell Properties")]
    [SerializeField] private Texture2D mBaseColor;
    [SerializeField] private int mDensity = 10;
    [SerializeField] int mLayers = 1;
    [SerializeField] float mHeight;
    [SerializeField] float mThickness = 1f;
    [SerializeField] float mNoiseOffset = 0f;

    [Header("Lighting")]
    [SerializeField] private float mAttenuation = 1f;
    [SerializeField] private float mOcclusionBias = 0f;
    [SerializeField] private float mMetallic = 0f;
    [SerializeField] private float mSmoothness = 0f;

    [Header("Physics")]
    [SerializeField] Vector3 mFixedForceDirection = Vector3.zero;
    [SerializeField] float mDisplacementStrength = 0.04f;
    [SerializeField] float mCurvature = 2.5f;
    [SerializeField] float mVelocitySmoothing = 10f;
    float mCurrentWindForce = 0f;

    private int mBaseColorProperty = Shader.PropertyToID("_BaseColor");
    private int mDensityProperty = Shader.PropertyToID("_Density");
    private int mThicknessProperty = Shader.PropertyToID("_Thickness");
    private int mAttenuationProperty = Shader.PropertyToID("_Attenuation");
    private int mOcclusionBiasProperty = Shader.PropertyToID("_OcclusionBias");
    private int mMetallicProperty = Shader.PropertyToID("_Metallic");
    private int mSmoothnessProperty = Shader.PropertyToID("_Smoothness");
    private int mNoiseOffsetProperty = Shader.PropertyToID("_NoiseOffset");

    Mesh mMesh;
    MeshRenderer mMeshRenderer;
    int mTriangleCount;

    Vector3 mPreviousPosition = Vector3.zero;
    Vector3 mTargetVelocity = Vector3.zero;
    Vector3 mCurrentVelocity = Vector3.zero;

    int mKernelID;
    int mThreadGroupSize;

    bool mIsInitialized = false;

    List<InputTriangle> mInputTriangles;

    private Material mMaterialInstance;
    private ComputeShader mComputeShaderInstance;

    ComputeBuffer mInputTrianglesBuffer;
    ComputeBuffer mDrawTrianglesBuffer;
    ComputeBuffer mIndirectArgumentsBuffer;

    int[] mIndirectArgs = { 0, 1, 0, 0 };

    //3 vertices, each vert has 8 floats for pos, normal, and uv
    const int INPUT_TRIANGLE_STRIDE = 3 * sizeof(float) * (3 + 3 + 2);
    const int DRAW_TRIANGLE_STRIDE = 3 * sizeof(float) * (3 + 3 + 2 + 4);
    const int INDIRECT_ARG_STRIDE = 4 * sizeof(int);

    private void Awake()
    {
        mPreviousPosition = transform.position;
        mTargetVelocity = Vector3.zero;

        Mesh mesh;
        if (GetComponent<MeshFilter>().sharedMesh)
        {
            mesh = GetComponent<MeshFilter>().sharedMesh;
        }
        else if (GetComponent<SkinnedMeshRenderer>().sharedMesh)
        {
            mesh = GetComponent<SkinnedMeshRenderer>().sharedMesh;
        }
        else
        {
            mesh = new Mesh();
        }

        mMesh = Instantiate(mesh);
        mMaterialInstance = new Material(mShellRenderMaterial);
        mComputeShaderInstance = Instantiate(mShellCompute);

    }

    private void OnEnable()
    {
        mMeshRenderer = GetComponent<MeshRenderer>();
        mTriangleCount = mMesh.triangles.Length / 3;
        SetupBuffers();
        GenerateGeometry();
        SetupComputeParameters();
    }

    private void LoadMesh()
    {
        GenerateGeometry();
        ShellMeshUtil.SaveMesh(mInputTriangles);

        string filePath = Path.Combine(Application.persistentDataPath, "savedShellMesh.json");
        mInputTriangles = ShellMeshUtil.LoadMesh(filePath);
    }

    private void OnValidate()
    {
        if (!mIsInitialized) return;

        RegenerateBuffers();
        
        mInputTrianglesBuffer.SetData(mInputTriangles);
        mDrawTrianglesBuffer.SetCounterValue(0);
        mIndirectArgumentsBuffer.SetData(mIndirectArgs);
        
        SetupComputeParameters();
    }

    private void OnDisable()
    {
        ReleaseBuffers();
    }

    private void Update()
    {
        if (mIsInitialized)
        {
            UpdateDynamicUniforms();
            mComputeShaderInstance.Dispatch(mKernelID, mThreadGroupSize, 1, 1);
            
            Graphics.DrawProceduralIndirect
                (
                    mMaterialInstance, mMeshRenderer.bounds, 
                    MeshTopology.Triangles, mIndirectArgumentsBuffer, 
                    0, null, null, 
                    UnityEngine.Rendering.ShadowCastingMode.Off, 
                    true, gameObject.layer
                );
        }
    }

    private void FixedUpdate()
    {
        CalculateVelocity();
    }

    private void CalculateVelocity()
    {
        mTargetVelocity = (transform.position - mPreviousPosition) / Time.deltaTime;
        mPreviousPosition = transform.position;

        mCurrentVelocity = Vector3.Lerp(mCurrentVelocity, mTargetVelocity, Time.deltaTime * mVelocitySmoothing);
    }

    private void UpdateDynamicUniforms()
    {
        mInputTrianglesBuffer.SetData(mInputTriangles);
        mDrawTrianglesBuffer.SetCounterValue(0);
        mIndirectArgumentsBuffer.SetData(mIndirectArgs);

        Vector3 moveDir = -transform.InverseTransformVector(mCurrentVelocity) + mFixedForceDirection;

        mComputeShaderInstance.SetVector("_MovementDirection", moveDir);
        mComputeShaderInstance.SetMatrix("_LocalToWorld", mMeshRenderer.localToWorldMatrix);
        float time = Time.time * 0.5f;
        mCurrentWindForce = (float)(Mathf.Cos(time) * Mathf.Cos(time * 1) * Mathf.Cos(time * 3) * Mathf.Cos(time * 5) * 0.5 + Mathf.Sin(time * 25) * 0.02);
        mComputeShaderInstance.SetFloat("_WindForce", mCurrentWindForce);
    }

    private void GenerateGeometry()
    {
        if (mMesh == null) return;

        mInputTriangles = new List<InputTriangle>();
        for (int i = 0; i < mTriangleCount; i++)
        {
            InputTriangle inputTri = new InputTriangle();
            mInputTriangles.Add(inputTri);
        }

        for (int i = 0; i < mMesh.triangles.Length; i += 3)
        {
            int tri = i / 3;

            InputTriangle tempTri = mInputTriangles[tri];
            
            tempTri.inputVert0.position = mMesh.vertices[mMesh.triangles[i]];
            tempTri.inputVert0.normal = mMesh.normals[mMesh.triangles[i]];
            tempTri.inputVert0.uv = mMesh.uv[mMesh.triangles[i]];

            tempTri.inputVert1.position = mMesh.vertices[mMesh.triangles[i + 1]];
            tempTri.inputVert1.normal = mMesh.normals[mMesh.triangles[i + 1]];
            tempTri.inputVert1.uv = mMesh.uv[mMesh.triangles[i + 1]];

            tempTri.inputVert2.position = mMesh.vertices[mMesh.triangles[i + 2]];
            tempTri.inputVert2.normal = mMesh.normals[mMesh.triangles[i + 2]];
            tempTri.inputVert2.uv = mMesh.uv[mMesh.triangles[i + 2]];

            mInputTriangles[tri] = tempTri;
        }

        mInputTrianglesBuffer.SetData(mInputTriangles);
        mDrawTrianglesBuffer.SetCounterValue(0);
        mIndirectArgumentsBuffer.SetData(mIndirectArgs);
    }

    private void SetupComputeParameters()
    {
        if (mMesh == null || mComputeShaderInstance == null || mMaterialInstance == null) return;

        mKernelID = mComputeShaderInstance.FindKernel("ShellTextureGeometry");
        mComputeShaderInstance.GetKernelThreadGroupSizes(mKernelID, out uint threadGroupSizeX, out _, out _);
        mThreadGroupSize = Mathf.CeilToInt((float)mTriangleCount / threadGroupSizeX);
        
        mComputeShaderInstance.SetBuffer(mKernelID, "_InputTrianglesBuffer", mInputTrianglesBuffer);
        mComputeShaderInstance.SetBuffer(mKernelID, "_DrawTrianglesBuffer", mDrawTrianglesBuffer);
        mComputeShaderInstance.SetBuffer(mKernelID, "_IndirectArgumentsBuffer", mIndirectArgumentsBuffer);

        mComputeShaderInstance.SetInt("_TriangleCount", mTriangleCount);

        mComputeShaderInstance.SetMatrix("_LocalToWorld", mMeshRenderer.localToWorldMatrix);

        mComputeShaderInstance.SetInt("_Layers", mLayers);
        mComputeShaderInstance.SetFloat("_Height", mHeight);

        //Compute Physics
        mComputeShaderInstance.SetVector("_MovementDirection", mTargetVelocity);
        mComputeShaderInstance.SetFloat("_DisplacementStrength", mDisplacementStrength);
        mComputeShaderInstance.SetFloat("_Curvature", mCurvature);
        mComputeShaderInstance.SetFloat("_WindForce", mCurrentWindForce);

        mMaterialInstance.SetBuffer("_DrawTrianglesBuffer", mDrawTrianglesBuffer);

        mComputeShaderInstance.Dispatch(mKernelID, mThreadGroupSize, 1, 1);
        
        mMaterialInstance.SetTexture(mBaseColorProperty, mBaseColor);
        mMaterialInstance.SetFloat(mDensityProperty, mDensity);
        mMaterialInstance.SetFloat(mThicknessProperty, mThickness);
        mMaterialInstance.SetFloat(mAttenuationProperty, mAttenuation);
        mMaterialInstance.SetFloat(mOcclusionBiasProperty, mOcclusionBias);
        mMaterialInstance.SetFloat(mMetallicProperty, mMetallic);
        mMaterialInstance.SetFloat(mSmoothnessProperty, mSmoothness);
        mMaterialInstance.SetFloat(mNoiseOffsetProperty, mNoiseOffset);

        mIsInitialized = true;
    }

    private void SetupBuffers()
    {
        mInputTrianglesBuffer = new ComputeBuffer(mTriangleCount, INPUT_TRIANGLE_STRIDE, ComputeBufferType.Structured, ComputeBufferMode.Immutable);
        mDrawTrianglesBuffer = new ComputeBuffer(mTriangleCount * mLayers, DRAW_TRIANGLE_STRIDE, ComputeBufferType.Append);
        mIndirectArgumentsBuffer = new ComputeBuffer(1, INDIRECT_ARG_STRIDE, ComputeBufferType.IndirectArguments);
    }

    private void ReleaseBuffer(ComputeBuffer buffer)
    {
        if (buffer != null)
        {
            buffer.Release();
            buffer = null;
        }
    }

    private void ReleaseBuffers()
    {
        ReleaseBuffer(mInputTrianglesBuffer);
        ReleaseBuffer(mDrawTrianglesBuffer);
        ReleaseBuffer(mIndirectArgumentsBuffer);
    }

    private void RegenerateBuffers()
    {
        ReleaseBuffers();
        SetupBuffers();
    }
}
