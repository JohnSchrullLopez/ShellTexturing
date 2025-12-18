using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Unity.VisualScripting;
using UnityEngine;

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct InputVertex
{
    public Vector3 position;
    public Vector3 normal;
    public Vector2 uv;
}

[Serializable, StructLayout(LayoutKind.Sequential)]
public struct InputTriangle
{
    public InputVertex inputVert0;
    public InputVertex inputVert1;
    public InputVertex inputVert2;
}

[Serializable]
public class InputTriangles
{
    [SerializeField]
    public List<InputTriangle> mInputTriangles;
}

public static class ShellMeshUtil
{
    public static InputTriangles mInputTriangles = new InputTriangles();

    public static string SaveMesh(List<InputTriangle> inputTriangles)
    {
        mInputTriangles.mInputTriangles = inputTriangles;

        string json = JsonUtility.ToJson(mInputTriangles, true);
        string filePath = Path.Combine(Application.persistentDataPath, "savedShellMesh.json");

        File.WriteAllText(filePath, json);

        return json;
    }

    public static List<InputTriangle> LoadMesh(string filePath)
    {
        string json = File.ReadAllText(filePath);
        return JsonUtility.FromJson<InputTriangles>(json).mInputTriangles;
    }
}
