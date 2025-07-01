using Unity.Collections;

namespace Voxels.DC
{
    public delegate void OnMeshGenerated(NativeArray<GPUVertex> vertices, int vertexCount, int vertexStartIndex, NativeArray<int> triangles, int triangleCount, int triangleStartIndex);
}