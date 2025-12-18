using System;
using UnityEngine;

//Temprary fix for base mesh clipping
//Replaces the material used by the base mesh with an invisible material
public class DisableBaseMesh : MonoBehaviour
{
    [SerializeField] private Material invisMaterial;
    private void Awake()
    {
        if (GetComponent<MeshRenderer>() != null)
        {
            GetComponent<MeshRenderer>().material = invisMaterial;
        }
        else if (GetComponent<SkinnedMeshRenderer>() != null)
        {
            GetComponent<SkinnedMeshRenderer>().material = invisMaterial;
        }
        else
        {
            throw new System.Exception("No MeshRenderer or SkinnedMeshRenderer");
        }
    }
}
