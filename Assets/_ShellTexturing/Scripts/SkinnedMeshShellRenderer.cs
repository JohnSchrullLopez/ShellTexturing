using System.Collections.Generic;
using System.IO;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;

public class SkinnedMeshShellRenderer : MonoBehaviour
{
    [SerializeField] Transform mRootBone;
    [SerializeField] ComputeShader mShellCompute;
    [SerializeField] Material mShellRenderMaterial;

    [Header("Shell Properties")]
    [SerializeField] private Texture2D mBaseColor;
    [SerializeField] private int mDensity = 10;
    [SerializeField] int mLayers = 1;
    [SerializeField] float mHeight;
    [SerializeField] float mThickness = 1f;
    [SerializeField] float mBias = 0.1f;
    [SerializeField] float mNoiseOffset = 0f;

    [Header("Lighting")]
    [SerializeField] private float mAttenuation = 1f;
    [SerializeField] private float mOcclusionBias = 0f;
    [SerializeField] private float mMetallic = 0f;
    [SerializeField] private float mSmoothness = 0f;

    [Header("Physics")]
    [SerializeField] Vector3 mGravityDirection = Vector3.zero;
    [SerializeField] float mDisplacementStrength = 0.04f;
    [SerializeField] float mCurvature = 2.5f;
    [SerializeField] float mVelocitySmoothing = 10f;

    private int mBaseColorProperty = Shader.PropertyToID("_BaseColor");
    private int mDensityProperty = Shader.PropertyToID("_Density");
    private int mThicknessProperty = Shader.PropertyToID("_Thickness");
    private int mAttenuationProperty = Shader.PropertyToID("_Attenuation");
    private int mOcclusionBiasProperty = Shader.PropertyToID("_OcclusionBias");
    private int mMetallicProperty = Shader.PropertyToID("_Metallic");
    private int mSmoothnessProperty = Shader.PropertyToID("_Smoothness");
    private int mNoiseOffsetProperty = Shader.PropertyToID("_NoiseOffset");

    Mesh mMesh;
    GraphicsBuffer mVertexBuffer;
    GraphicsBuffer mUVBuffer;
    SkinnedMeshRenderer mMeshRenderer;
    int mTriangleCount;

    Vector3 mPreviousPosition = Vector3.zero;
    Vector3 mTargetVelocity = Vector3.zero;
    Vector3 mCurrentVelocity = Vector3.zero;

    int mKernelID;
    int mThreadGroupSize;

    bool mIsInitialized = false;

    private Material mMaterialInstance;
    private ComputeShader mComputeShaderInstance;

    ComputeBuffer mDrawTrianglesBuffer;
    ComputeBuffer mIndirectArgumentsBuffer;
    private ComputeBuffer mIndexBuffer;

    int[] mIndirectArgs = { 0, 1, 0, 0 };

    //3 vertices, each vert has 8 floats for pos, normal, and uv
    private const int INDEX_BUFFER_STRIDE = 2 * sizeof(int);
    const int DRAW_TRIANGLE_STRIDE = 3 * sizeof(float) * (3 + 3 + 2 + 4);
    const int INDIRECT_ARG_STRIDE = 4 * sizeof(int);

    private void Awake()
    {
        mPreviousPosition = transform.position;
        mTargetVelocity = Vector3.zero;

        mMeshRenderer = GetComponent<SkinnedMeshRenderer>();

        Mesh mesh = mMeshRenderer.sharedMesh;
        mMesh = Instantiate(mesh);
        mMesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
        mMesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;

        //index is the stream to get
        mVertexBuffer = mMesh.GetVertexBuffer(0);
        mUVBuffer = mMesh.GetVertexBuffer(1);

        VertexAttributeDescriptor[] attributes = mMesh.GetVertexAttributes();

        mMaterialInstance = new Material(mShellRenderMaterial);
        mComputeShaderInstance = Instantiate(mShellCompute);
    }

    private void PrintAttributeDetails()
    {
        VertexAttributeDescriptor[] descriptors = mMesh.GetVertexAttributes();
        foreach (VertexAttributeDescriptor descriptor in descriptors)
        {
            Debug.Log(
                $"Attr: {descriptor.attribute}\n" +
                $"Dimension: {descriptor.dimension}\n" +
                $"Stream: {descriptor.stream}\n" +
                $"Offset: {mMesh.GetVertexAttributeOffset(descriptor.attribute)}");
        }
    }

    private void OnEnable()
    {
        //mMeshRenderer = GetComponent<MeshRenderer>();
        
        mTriangleCount = mMesh.triangles.Length / 3;
        SetupBuffers();
        GenerateGeometry();
        
        SetupComputeParameters();
    }

    private void OnValidate()
    {
        if (!mIsInitialized) return;

        RegenerateBuffers();

        mDrawTrianglesBuffer.SetCounterValue(0);
        mIndirectArgumentsBuffer.SetData(mIndirectArgs);
        mIndexBuffer.SetData(mMesh.triangles);
        
        SetupComputeParameters();
    }

    private void OnDisable()
    {
        ReleaseBuffers();
    }

    private void LateUpdate()
    {
        CalculateVelocity();

        if (mIsInitialized)
        {
            UpdateDynamicUniforms();
            mComputeShaderInstance.Dispatch(mKernelID, mThreadGroupSize, 1, 1);

            Graphics.DrawProceduralIndirect
                (
                    mMaterialInstance, mMeshRenderer.bounds,
                    MeshTopology.Triangles, mIndirectArgumentsBuffer,
                    0, null, null,
                    UnityEngine.Rendering.ShadowCastingMode.On,
                    true, gameObject.layer
                );
        }
    }

    private void CalculateVelocity()
    {
        mTargetVelocity = (transform.position - mPreviousPosition) / Time.deltaTime;
        mPreviousPosition = transform.position;

        mCurrentVelocity = Vector3.Lerp(mCurrentVelocity, mTargetVelocity, Time.deltaTime * mVelocitySmoothing);
    }

    private void UpdateDynamicUniforms()
    {
        GraphicsBuffer vertBuffer = mMeshRenderer.GetVertexBuffer();

        if (vertBuffer != null)
        {
            mVertexBuffer = vertBuffer;

            //Converts from bone local space -> world -> mesh renderer local -> world
            //Fixes an issue where meshRenderer.GetVertexBuffer() returns the wrong local space
            Matrix4x4 spaceConversion = mMeshRenderer.localToWorldMatrix * mMeshRenderer.worldToLocalMatrix * mRootBone.localToWorldMatrix;
            mComputeShaderInstance.SetMatrix("_LocalToWorld", spaceConversion);
        }

        mDrawTrianglesBuffer.SetCounterValue(0);
        mIndirectArgumentsBuffer.SetData(mIndirectArgs);

        Vector3 moveDir = -mRootBone.transform.InverseTransformVector(mCurrentVelocity) + mGravityDirection;

        mComputeShaderInstance.SetVector("_MovementDirection", moveDir);
        mComputeShaderInstance.SetBuffer(mKernelID, "_SkinnedVertexBuffer", mVertexBuffer);
    }

    private void GenerateGeometry()
    {
        if (mMesh == null) return;

        mIndexBuffer.SetData(mMesh.triangles);
        mDrawTrianglesBuffer.SetCounterValue(0);
        mIndirectArgumentsBuffer.SetData(mIndirectArgs);
    }

    private void SetupComputeParameters()
    {
        if (mMesh == null || mComputeShaderInstance == null || mMaterialInstance == null) return;

        mKernelID = mComputeShaderInstance.FindKernel("SkinnedShellTextureGeometry");
        mComputeShaderInstance.GetKernelThreadGroupSizes(mKernelID, out uint threadGroupSizeX, out _, out _);
        mThreadGroupSize = Mathf.CeilToInt(mTriangleCount / threadGroupSizeX);

        mComputeShaderInstance.SetBuffer(mKernelID, "_IndicesBuffer", mIndexBuffer);
        mComputeShaderInstance.SetBuffer(mKernelID, "_SkinnedVertexBuffer", mVertexBuffer);
        mComputeShaderInstance.SetBuffer(mKernelID, "_UVBuffer", mUVBuffer);
        mComputeShaderInstance.SetBuffer(mKernelID, "_DrawTrianglesBuffer", mDrawTrianglesBuffer);
        mComputeShaderInstance.SetBuffer(mKernelID, "_IndirectArgumentsBuffer", mIndirectArgumentsBuffer);

        mComputeShaderInstance.SetInt("_NormalOffset", mMesh.GetVertexAttributeOffset(mMesh.GetVertexAttribute(1).attribute));
        mComputeShaderInstance.SetInt("_Stride", mMesh.GetVertexBufferStride(0));
        mComputeShaderInstance.SetInt("_UVStride", mMesh.GetVertexBufferStride(1));
        mComputeShaderInstance.SetInt("_UVOffset", mMesh.GetVertexAttributeOffset(mMesh.GetVertexAttribute(4).attribute));

        mComputeShaderInstance.SetInt("_TriangleCount", mTriangleCount);

        mComputeShaderInstance.SetMatrix("_LocalToWorld", mMeshRenderer.localToWorldMatrix);

        mComputeShaderInstance.SetInt("_Layers", mLayers);
        mComputeShaderInstance.SetFloat("_Height", mHeight);

        //Compute Physics
        mComputeShaderInstance.SetVector("_MovementDirection", mTargetVelocity);
        mComputeShaderInstance.SetFloat("_DisplacementStrength", mDisplacementStrength);
        mComputeShaderInstance.SetFloat("_Curvature", mCurvature);
        mComputeShaderInstance.SetFloat("_Bias", mBias);

        mComputeShaderInstance.Dispatch(mKernelID, mThreadGroupSize, 1, 1);
        
        mMaterialInstance.SetBuffer("_DrawTrianglesBuffer", mDrawTrianglesBuffer);
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
        mDrawTrianglesBuffer = new ComputeBuffer(mTriangleCount * mLayers, DRAW_TRIANGLE_STRIDE, ComputeBufferType.Append);
        mIndirectArgumentsBuffer = new ComputeBuffer(1, INDIRECT_ARG_STRIDE, ComputeBufferType.IndirectArguments);
        mIndexBuffer = new ComputeBuffer(mMesh.triangles.Length, sizeof(int), ComputeBufferType.Structured, ComputeBufferMode.Immutable);
        mIndexBuffer.SetData(mMesh.triangles);
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
        //ReleaseBuffer(mInputTrianglesBuffer);
        ReleaseBuffer(mDrawTrianglesBuffer);
        ReleaseBuffer(mIndirectArgumentsBuffer);
        ReleaseBuffer(mIndexBuffer);
    }

    private void RegenerateBuffers()
    {
        ReleaseBuffers();
        SetupBuffers();
    }
}
