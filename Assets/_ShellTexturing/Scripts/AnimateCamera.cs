using System;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Experimental.GlobalIllumination;

public class AnimateCamera : MonoBehaviour
{
    [SerializeField] private Vector3 mLightOffset;
    [SerializeField] private float mRotateSpeed;
    private CinemachineOrbitalFollow mOrbitCam;
    [SerializeField] private Transform mDirectionalLight;
    [SerializeField] private Transform mTarget;

    private void Awake()
    {
        mOrbitCam = GetComponent<CinemachineOrbitalFollow>();
    }

    private void Update()
    {
        mOrbitCam.HorizontalAxis.Value += Time.deltaTime * mRotateSpeed;
        mDirectionalLight.position = mOrbitCam.transform.position + mLightOffset;
        mDirectionalLight.LookAt(mTarget, Vector3.up);
    }
}
