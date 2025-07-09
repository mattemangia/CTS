//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using CTS.Misc;
using System.Linq;

namespace CTS
{
    public class MeshGenerator
    {
        // Basic mesh structure
        public class Mesh
        {
            public List<Vector3> Vertices { get; set; }
            public List<int> Indices { get; set; }
            public List<Vector3> Normals { get; set; }

            public Mesh()
            {
                Vertices = new List<Vector3>();
                Indices = new List<int>();
                Normals = new List<Vector3>();
            }
        }

        // Input data
        private ILabelVolumeData volumeLabels;
        private byte materialID;
        private int width, height, depth;
        private int targetFacets;
        private IProgress<int> progress;

        // Neighbor directions for checking voxel connectivity
        private static readonly int[,] NeighborOffsets = new int[6, 3]
        {
            { 1, 0, 0 },  // +X
            {-1, 0, 0 },  // -X
            { 0, 1, 0 },  // +Y
            { 0,-1, 0 },  // -Y
            { 0, 0, 1 },  // +Z
            { 0, 0,-1 }   // -Z
        };

        public MeshGenerator(ILabelVolumeData volumeLabels, byte materialID, int width, int height, int depth, int targetFacets, IProgress<int> progress)
        {
            this.volumeLabels = volumeLabels;
            this.materialID = materialID;
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.targetFacets = targetFacets;
            this.progress = progress;
        }

        public async Task<Mesh> GenerateMeshAsync(CancellationToken cancellationToken)
        {
            return await Task.Run(() => GenerateMesh(cancellationToken), cancellationToken);
        }

        private Mesh GenerateMesh(CancellationToken cancellationToken)
        {
            // Start with a high-resolution mesh (all faces)
            Logger.Log("[MeshGenerator] Starting mesh generation");
            progress?.Report(0);

            Mesh fullMesh = CreateFullResolutionMesh(cancellationToken);

            // Count current facets
            int currentFacets = fullMesh.Indices.Count / 3;
            Logger.Log($"[MeshGenerator] Generated full resolution mesh with {currentFacets} facets");

            progress?.Report(50);

            // If needed, simplify the mesh to target facet count
            if (currentFacets > targetFacets)
            {
                Logger.Log($"[MeshGenerator] Simplifying mesh to target of {targetFacets} facets");
                fullMesh = SimplifyMesh(fullMesh, targetFacets, cancellationToken);
            }

            progress?.Report(90);

            // Calculate normals
            CalculateNormals(fullMesh);

            progress?.Report(100);
            Logger.Log($"[MeshGenerator] Mesh generation complete. Final mesh has {fullMesh.Indices.Count / 3} triangles.");

            return fullMesh;
        }

        private Mesh CreateFullResolutionMesh(CancellationToken cancellationToken)
        {
            Mesh mesh = new Mesh();
            Dictionary<Vector3, int> vertexMap = new Dictionary<Vector3, int>();

            // Step 1: Identify all voxels of the material
            bool[,,] materialVoxels = new bool[width, height, depth];

            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        materialVoxels[x, y, z] = volumeLabels[x, y, z] == materialID;
                    }
                }
            });

            // Step 2: Create a padded volume to handle boundary checks more easily
            bool[,,] paddedVolume = new bool[width + 2, height + 2, depth + 2];

            // Copy material voxels to padded volume
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        paddedVolume[x + 1, y + 1, z + 1] = materialVoxels[x, y, z];
                    }
                }
            }

            // Step 3: Process each voxel in the original volume
            int processedVoxels = 0;
            int totalVoxels = width * height * depth;

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Only process voxels of our material
                        if (materialVoxels[x, y, z])
                        {
                            // Check each of the 6 neighboring faces
                            for (int i = 0; i < 6; i++)
                            {
                                int nx = x + 1 + NeighborOffsets[i, 0];
                                int ny = y + 1 + NeighborOffsets[i, 1];
                                int nz = z + 1 + NeighborOffsets[i, 2];

                                // If neighbor is not our material, add a face (this handles both external 
                                // surfaces and internal cavities/pores)
                                if (!paddedVolume[nx, ny, nz])
                                {
                                    // Add face for this side of the voxel
                                    AddVoxelFace(mesh, vertexMap, x, y, z, i);
                                }
                            }
                        }

                        // Update progress periodically
                        processedVoxels++;
                        if (processedVoxels % 10000 == 0)
                        {
                            int progressValue = (int)(processedVoxels / (float)totalVoxels * 50);
                            progress?.Report(progressValue);

                            if (cancellationToken.IsCancellationRequested)
                                throw new OperationCanceledException();
                        }
                    }
                }
            }

            return mesh;
        }
        private int ProcessCollapsesOptimized(MeshData meshData, VertexState[] vertexState,
                                    List<EdgeCollapse> collapses, int targetCount,
                                    CancellationToken token)
        {
            int processed = 0;
            int maxToProcess = Math.Min(targetCount, collapses.Count);

            // Use an array to track which vertices are already being processed
            bool[] vertexLocked = new bool[vertexState.Length];

            // Process collapses in batches to avoid thread contention
            int batchSize = 1000;
            for (int batchStart = 0; batchStart < maxToProcess && processed < targetCount; batchStart += batchSize)
            {
                if (token.IsCancellationRequested) break;

                int batchEnd = Math.Min(batchStart + batchSize, maxToProcess);
                int batchProcessed = 0;

                Parallel.For(batchStart, batchEnd, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = token
                },
                    i => {
                        var collapse = collapses[i];
                        int v1 = collapse.VertexA;
                        int v2 = collapse.VertexB;

                        // Skip if either vertex is already removed or locked
                        if (vertexState[v1].IsRemoved || vertexState[v2].IsRemoved) return;

                        // Try to lock both vertices atomically
                        bool locked = false;
                        lock (vertexLocked)
                        {
                            if (!vertexLocked[v1] && !vertexLocked[v2])
                            {
                                vertexLocked[v1] = true;
                                vertexLocked[v2] = true;
                                locked = true;
                            }
                        }

                        if (!locked) return;

                        try
                        {
                            // Apply collapse - v2 gets collapsed into v1
                            // First update position and quadric of v1
                            vertexState[v1].Position = collapse.TargetPosition;
                            vertexState[v1].Quadric.Add(vertexState[v2].Quadric);

                            // Then mark v2 as removed
                            vertexState[v2].IsRemoved = true;

                            // Update triangle vertex indices (replace v2 with v1)
                            UpdateTriangles(meshData, v1, v2);

                            Interlocked.Increment(ref batchProcessed);
                        }
                        finally
                        {
                            // Unlock vertices
                            lock (vertexLocked)
                            {
                                vertexLocked[v1] = false;
                                vertexLocked[v2] = false;
                            }
                        }
                    }
                );

                processed += batchProcessed;

                // If we made very little progress, stop to avoid wasting time
                if (batchProcessed < batchSize / 10)
                {
                    Logger.Log($"[MeshGenerator] Early exit - diminishing returns (only {batchProcessed} successful collapses)");
                    break;
                }
            }

            return processed;
        }
        private List<EdgeCollapse> SampleEdgeCollapsesForLargeMesh(MeshData meshData, VertexState[] vertexState,
                                                         int targetSamples, CancellationToken token)
        {
            var collapsesBag = new System.Collections.Concurrent.ConcurrentBag<EdgeCollapse>();
            var processedEdges = new System.Collections.Concurrent.ConcurrentDictionary<long, byte>();

            int triangleCount = meshData.TriangleCount;
            int sampleStep = Math.Max(1, triangleCount / (targetSamples * 2));

            // Use sampling to handle very large meshes
            Parallel.For(0, triangleCount, new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = token
            },
                triIdx => {
                    // Sample only a subset of triangles
                    if (triIdx % sampleStep != 0) return;

                    if (token.IsCancellationRequested) return;

                    if (meshData.IsTriangleValid[triIdx])
                    {
                        int baseIdx = triIdx * 3;
                        int v1 = meshData.Indices[baseIdx];
                        int v2 = meshData.Indices[baseIdx + 1];
                        int v3 = meshData.Indices[baseIdx + 2];

                        // Only process one edge per triangle to reduce workload
                        ProcessEdge(v1, v2, vertexState, collapsesBag, processedEdges);
                    }
                }
            );

            var result = collapsesBag.ToList();
            Logger.Log($"[MeshGenerator] Sampled {result.Count} potential edge collapses");
            return result;
        }
        private void PartialSort(List<EdgeCollapse> list, int startIndex, int count)
        {
            // Only sort the portion we need
            int endIndex = Math.Min(startIndex + count, list.Count);

            // Use a faster sorting approach for large lists
            if (list.Count > 100000)
            {
                // Extract just the portion we want to sort
                var sublist = list.GetRange(startIndex, endIndex - startIndex);
                sublist.Sort((a, b) => a.Error.CompareTo(b.Error));

                // Copy back sorted portion
                for (int i = 0; i < sublist.Count; i++)
                {
                    list[startIndex + i] = sublist[i];
                }
            }
            else
            {
                // For smaller lists, use regular sort
                list.Sort((a, b) => a.Error.CompareTo(b.Error));
            }
        }
        private void UpdateTriangles(MeshData meshData, int keepVertex, int removeVertex)
        {
            // Local update for better performance
            int[] indices = meshData.Indices;
            bool[] isValid = meshData.IsTriangleValid;

            for (int triIdx = 0; triIdx < meshData.TriangleCount; triIdx++)
            {
                if (!isValid[triIdx]) continue;

                int baseIdx = triIdx * 3;

                // Replace removed vertex with kept vertex
                bool containsRemovedVertex = false;
                for (int i = 0; i < 3; i++)
                {
                    if (indices[baseIdx + i] == removeVertex)
                    {
                        indices[baseIdx + i] = keepVertex;
                        containsRemovedVertex = true;
                    }
                }

                // Check for degenerate triangle (all indices same)
                if (containsRemovedVertex)
                {
                    if (indices[baseIdx] == indices[baseIdx + 1] ||
                        indices[baseIdx + 1] == indices[baseIdx + 2] ||
                        indices[baseIdx + 2] == indices[baseIdx])
                    {
                        isValid[triIdx] = false;
                    }
                }
            }
        }
        private void AddVoxelFace(Mesh mesh, Dictionary<Vector3, int> vertexMap, int x, int y, int z, int faceDirection)
        {
            // Define vertices for each face of a unit cube
            // These are ordered counter-clockwise for proper normals
            Vector3[][] faceVertices = new Vector3[6][]
            {
                // +X face
                new Vector3[] {
                    new Vector3(x+1, y, z),
                    new Vector3(x+1, y+1, z),
                    new Vector3(x+1, y+1, z+1),
                    new Vector3(x+1, y, z+1)
                },
                // -X face
                new Vector3[] {
                    new Vector3(x, y, z+1),
                    new Vector3(x, y+1, z+1),
                    new Vector3(x, y+1, z),
                    new Vector3(x, y, z)
                },
                // +Y face
                new Vector3[] {
                    new Vector3(x, y+1, z),
                    new Vector3(x, y+1, z+1),
                    new Vector3(x+1, y+1, z+1),
                    new Vector3(x+1, y+1, z)
                },
                // -Y face
                new Vector3[] {
                    new Vector3(x+1, y, z),
                    new Vector3(x+1, y, z+1),
                    new Vector3(x, y, z+1),
                    new Vector3(x, y, z)
                },
                // +Z face
                new Vector3[] {
                    new Vector3(x, y, z+1),
                    new Vector3(x+1, y, z+1),
                    new Vector3(x+1, y+1, z+1),
                    new Vector3(x, y+1, z+1)
                },
                // -Z face
                new Vector3[] {
                    new Vector3(x, y+1, z),
                    new Vector3(x+1, y+1, z),
                    new Vector3(x+1, y, z),
                    new Vector3(x, y, z)
                }
            };

            // Get the four corners of this face
            Vector3[] corners = faceVertices[faceDirection];

            // Add vertices to mesh (reusing existing vertices)
            int[] indices = new int[4];
            for (int i = 0; i < 4; i++)
            {
                Vector3 v = corners[i];

                if (vertexMap.TryGetValue(v, out int index))
                {
                    indices[i] = index;
                }
                else
                {
                    indices[i] = mesh.Vertices.Count;
                    mesh.Vertices.Add(v);
                    vertexMap[v] = indices[i];
                }
            }

            // Add two triangles to form the face (counter-clockwise winding)
            // Triangle 1: 0-1-2
            mesh.Indices.Add(indices[0]);
            mesh.Indices.Add(indices[1]);
            mesh.Indices.Add(indices[2]);

            // Triangle 2: 0-2-3
            mesh.Indices.Add(indices[0]);
            mesh.Indices.Add(indices[2]);
            mesh.Indices.Add(indices[3]);
        }

        private Mesh SimplifyMesh(Mesh inputMesh, int targetFacets, CancellationToken cancellationToken)
        {
            int currentFacets = inputMesh.Indices.Count / 3;
            if (currentFacets <= targetFacets) return inputMesh;

            Logger.Log($"[MeshGenerator] Starting parallel QEM-based mesh simplification from {currentFacets} to {targetFacets} facets");

            // Create a working copy of the mesh
            Mesh workingMesh = new Mesh();
            workingMesh.Vertices = new List<Vector3>(inputMesh.Vertices);
            workingMesh.Indices = new List<int>(inputMesh.Indices);

            // Track the remaining number of triangles
            int remainingTriangles = currentFacets;
            int collapsesNeeded = remainingTriangles - targetFacets;

            // Create vertex metadata and state for tracking changes
            var vertexState = new VertexState[workingMesh.Vertices.Count];
            Parallel.For(0, vertexState.Length, i => {
                vertexState[i] = new VertexState
                {
                    Position = workingMesh.Vertices[i],
                    IsRemoved = false,
                    Quadric = new SymmetricMatrix()
                };
            });

            // Build mesh connectivity and compute initial quadrics in parallel
            var meshData = new MeshData(workingMesh);
            ComputeQuadricsParallel(meshData, vertexState, cancellationToken);

            // Use a hybrid approach - incremental simplification for large meshes
            // Calculate collapses per batch (5-10% of total collapses needed per batch)
            int collapsesPerBatch = Math.Max(10000, collapsesNeeded / 10);
            int batchesDone = 0;
            int maxBatches = 50; // Limit total batches

            while (remainingTriangles > targetFacets && batchesDone < maxBatches && !cancellationToken.IsCancellationRequested)
            {
                // Report progress
                int progressValue = 50 + (int)((currentFacets - remainingTriangles) / (float)collapsesNeeded * 40);
                progress?.Report(progressValue);

                int batchTarget = Math.Min(collapsesPerBatch, remainingTriangles - targetFacets);

                Logger.Log($"[MeshGenerator] Simplification batch {batchesDone + 1}, " +
                          $"triangles: {remainingTriangles}, target this batch: {batchTarget}");

                // For large meshes, use sampling to find edges to collapse
                var collapses = SampleEdgeCollapsesForLargeMesh(meshData, vertexState, batchTarget * 2, cancellationToken);

                if (collapses.Count == 0)
                {
                    Logger.Log("[MeshGenerator] No valid collapses found, exiting simplification");
                    break;
                }

                // Sort just the top candidates by error (partial sort for efficiency)
                int candidatesToProcess = Math.Min(collapses.Count, batchTarget * 2);
                PartialSort(collapses, 0, candidatesToProcess);

                // Process a batch of edge collapses
                int processed = ProcessCollapsesOptimized(meshData, vertexState, collapses, batchTarget, cancellationToken);

                Logger.Log($"[MeshGenerator] Processed {processed} collapses in this batch");

                // Update remaining triangle count
                remainingTriangles = CountRemainingTriangles(meshData);

                // Exit if we couldn't make progress
                if (processed == 0) break;

                batchesDone++;
            }

            // Apply all changes to create the final mesh
            Mesh finalMesh = ReconstructMeshFromState(meshData, vertexState);

            Logger.Log($"[MeshGenerator] Parallel mesh simplification complete. Final mesh has {finalMesh.Indices.Count / 3} triangles.");

            return finalMesh;
        }

        private void ComputeQuadricsParallel(MeshData meshData, VertexState[] vertexState, CancellationToken cancellationToken)
        {
            // Step 1: Compute triangle quadrics in parallel
            var triangleQuadrics = new SymmetricMatrix[meshData.TriangleCount];

            Parallel.For(0, meshData.TriangleCount, new ParallelOptions { CancellationToken = cancellationToken }, triIdx => {
                if (meshData.IsTriangleValid[triIdx])
                {
                    int baseIdx = triIdx * 3;
                    int v1 = meshData.Indices[baseIdx];
                    int v2 = meshData.Indices[baseIdx + 1];
                    int v3 = meshData.Indices[baseIdx + 2];

                    Vector3 p1 = vertexState[v1].Position;
                    Vector3 p2 = vertexState[v2].Position;
                    Vector3 p3 = vertexState[v3].Position;

                    // Calculate triangle plane
                    Vector3 normal = Vector3.Normalize(Vector3.Cross(p2 - p1, p3 - p1));
                    float d = -Vector3.Dot(normal, p1);

                    // Create quadric matrix for this plane
                    triangleQuadrics[triIdx] = new SymmetricMatrix(
                        normal.X, normal.Y, normal.Z, d,
                        normal.X, normal.Y, normal.Z, d);
                }
            });

            // Step 2: Accumulate quadrics to vertices in parallel with less locking
            // Use a more efficient approach for large meshes
            int vertexCount = vertexState.Length;

            // Create local quadric accumulators for each thread to avoid locking
            var localQuadrics = new System.Collections.Concurrent.ConcurrentDictionary<int,
                System.Collections.Concurrent.ConcurrentQueue<SymmetricMatrix>>();

            Parallel.For(0, meshData.TriangleCount, new ParallelOptions { CancellationToken = cancellationToken }, triIdx => {
                if (meshData.IsTriangleValid[triIdx])
                {
                    int baseIdx = triIdx * 3;
                    int v1 = meshData.Indices[baseIdx];
                    int v2 = meshData.Indices[baseIdx + 1];
                    int v3 = meshData.Indices[baseIdx + 2];

                    // Queue quadric to each vertex's accumulator
                    QueueQuadric(localQuadrics, v1, triangleQuadrics[triIdx]);
                    QueueQuadric(localQuadrics, v2, triangleQuadrics[triIdx]);
                    QueueQuadric(localQuadrics, v3, triangleQuadrics[triIdx]);
                }
            });

            // Combine the local quadrics for each vertex
            Parallel.For(0, vertexCount, new ParallelOptions { CancellationToken = cancellationToken }, vertIdx => {
                if (localQuadrics.TryGetValue(vertIdx, out var queue))
                {
                    while (queue.TryDequeue(out var quadric))
                    {
                        vertexState[vertIdx].Quadric.Add(quadric);
                    }
                }
            });
        }
        private List<EdgeCollapse> FindValidCollapsesParallel(MeshData meshData, VertexState[] vertexState, CancellationToken token)
        {
            // Create a ConcurrentBag to collect edge collapses from all threads
            var collapsesBag = new System.Collections.Concurrent.ConcurrentBag<EdgeCollapse>();

            // Create a set of edges we've processed to avoid duplicates
            var processedEdges = new System.Collections.Concurrent.ConcurrentDictionary<long, byte>();

            // Process all edges in parallel
            Parallel.For(0, meshData.TriangleCount, triIdx => {
                if (token.IsCancellationRequested) return;

                if (meshData.IsTriangleValid[triIdx])
                {
                    int baseIdx = triIdx * 3;
                    int v1 = meshData.Indices[baseIdx];
                    int v2 = meshData.Indices[baseIdx + 1];
                    int v3 = meshData.Indices[baseIdx + 2];

                    // Process each edge of the triangle
                    ProcessEdge(v1, v2, vertexState, collapsesBag, processedEdges);
                    ProcessEdge(v2, v3, vertexState, collapsesBag, processedEdges);
                    ProcessEdge(v3, v1, vertexState, collapsesBag, processedEdges);
                }
            });

            return collapsesBag.ToList();
        }

        private void ProcessEdge(int v1, int v2, VertexState[] vertexState,
                         System.Collections.Concurrent.ConcurrentBag<EdgeCollapse> collapses,
                         System.Collections.Concurrent.ConcurrentDictionary<long, byte> processedEdges)
        {
            // Skip if either vertex is already removed
            if (vertexState[v1].IsRemoved || vertexState[v2].IsRemoved)
                return;

            // Ensure consistent edge direction for lookup
            int edgeV1 = Math.Min(v1, v2);
            int edgeV2 = Math.Max(v1, v2);

            // Create a unique key for this edge
            long edgeKey = ((long)edgeV1 << 32) | (uint)edgeV2;

            // Skip if we've already processed this edge
            if (!processedEdges.TryAdd(edgeKey, 0))
                return;

            // Compute optimal collapse position and error
            SymmetricMatrix combinedQuadric = new SymmetricMatrix(vertexState[v1].Quadric);
            combinedQuadric.Add(vertexState[v2].Quadric);

            // Try to find optimal position
            Vector3 optimalPos;
            bool foundOptimal = combinedQuadric.TryComputeOptimalPoint(out optimalPos);

            // If we can't find optimal position, use midpoint
            if (!foundOptimal)
            {
                optimalPos = (vertexState[v1].Position + vertexState[v2].Position) * 0.5f;
            }

            // Calculate error at optimal position
            float error = combinedQuadric.Evaluate(optimalPos);

            // Create collapse information
            collapses.Add(new EdgeCollapse
            {
                VertexA = v1,
                VertexB = v2,
                TargetPosition = optimalPos,
                Error = error
            });
        }
        private void QueueQuadric(System.Collections.Concurrent.ConcurrentDictionary<int,
    System.Collections.Concurrent.ConcurrentQueue<SymmetricMatrix>> localQuadrics, int vertexIdx, SymmetricMatrix quadric)
        {
            // Get or create a queue for this vertex
            var queue = localQuadrics.GetOrAdd(vertexIdx, _ =>
                new System.Collections.Concurrent.ConcurrentQueue<SymmetricMatrix>());

            // Queue the quadric
            queue.Enqueue(new SymmetricMatrix(quadric));
        }

        private int ProcessCollapsesInParallel(MeshData meshData, VertexState[] vertexState,
                                              List<EdgeCollapse> collapses, int targetCount,
                                              CancellationToken token)
        {
            // Create partition of collapses with low mutex contention
            // Group by "regions" to minimize conflicts
            int regionSize = (int)Math.Sqrt(vertexState.Length) + 1; // Heuristic for region size
            var collapsesByRegion = new Dictionary<int, List<EdgeCollapse>>();

            foreach (var collapse in collapses)
            {
                if (token.IsCancellationRequested) return 0;

                // If either vertex is already removed, skip
                if (vertexState[collapse.VertexA].IsRemoved || vertexState[collapse.VertexB].IsRemoved)
                    continue;

                // Get region ID (using first vertex as region key)
                int regionId = collapse.VertexA / regionSize;

                if (!collapsesByRegion.TryGetValue(regionId, out var regionCollapses))
                {
                    regionCollapses = new List<EdgeCollapse>();
                    collapsesByRegion[regionId] = regionCollapses;
                }

                regionCollapses.Add(collapse);
            }

            // Process each region's collapses sequentially to avoid conflicts
            int totalProcessed = 0;

            Parallel.ForEach(collapsesByRegion, regionEntry => {
                if (token.IsCancellationRequested) return;

                int regionProcessed = 0;
                int regionTarget = targetCount / collapsesByRegion.Count + 1;

                foreach (var collapse in regionEntry.Value)
                {
                    // Stop if we've reached target for this region
                    if (regionProcessed >= regionTarget) break;

                    // Skip if either vertex was removed by another collapse
                    if (vertexState[collapse.VertexA].IsRemoved || vertexState[collapse.VertexB].IsRemoved)
                        continue;

                    // Apply the collapse
                    vertexState[collapse.VertexA].Position = collapse.TargetPosition;
                    vertexState[collapse.VertexA].Quadric.Add(vertexState[collapse.VertexB].Quadric);

                    // Mark the removed vertex
                    vertexState[collapse.VertexB].IsRemoved = true;

                    // Update triangle references - mark v2 triangles as invalid and update vertices
                    for (int triIdx = 0; triIdx < meshData.TriangleCount; triIdx++)
                    {
                        if (!meshData.IsTriangleValid[triIdx]) continue;

                        int baseIdx = triIdx * 3;

                        // Check if triangle uses the removed vertex
                        bool containsRemovedVertex = false;
                        for (int i = 0; i < 3; i++)
                        {
                            if (meshData.Indices[baseIdx + i] == collapse.VertexB)
                            {
                                meshData.Indices[baseIdx + i] = collapse.VertexA;
                                containsRemovedVertex = true;
                            }
                        }

                        // Check if triangle became degenerate (all vertices same)
                        if (containsRemovedVertex)
                        {
                            int v1 = meshData.Indices[baseIdx];
                            int v2 = meshData.Indices[baseIdx + 1];
                            int v3 = meshData.Indices[baseIdx + 2];

                            if (v1 == v2 || v2 == v3 || v3 == v1)
                            {
                                meshData.IsTriangleValid[triIdx] = false;
                            }
                        }
                    }

                    regionProcessed++;
                    Interlocked.Increment(ref totalProcessed);
                }
            });

            return totalProcessed;
        }

        private int CountRemainingTriangles(MeshData meshData)
        {
            int count = 0;
            for (int i = 0; i < meshData.IsTriangleValid.Length; i++)
            {
                if (meshData.IsTriangleValid[i]) count++;
            }
            return count;
        }

        private Mesh ReconstructMeshFromState(MeshData meshData, VertexState[] vertexState)
        {
            // Create a new compact mesh
            Mesh result = new Mesh();

            // Create new vertices array without removed vertices
            List<Vector3> newVertices = new List<Vector3>();
            int[] vertexRemap = new int[vertexState.Length];

            for (int i = 0; i < vertexState.Length; i++)
            {
                if (!vertexState[i].IsRemoved)
                {
                    vertexRemap[i] = newVertices.Count;
                    newVertices.Add(vertexState[i].Position);
                }
                else
                {
                    vertexRemap[i] = -1;
                }
            }

            result.Vertices = newVertices;

            // Add all valid triangles with remapped vertex indices
            for (int triIdx = 0; triIdx < meshData.TriangleCount; triIdx++)
            {
                if (meshData.IsTriangleValid[triIdx])
                {
                    int baseIdx = triIdx * 3;
                    int v1 = meshData.Indices[baseIdx];
                    int v2 = meshData.Indices[baseIdx + 1];
                    int v3 = meshData.Indices[baseIdx + 2];

                    // Add triangle with remapped indices
                    result.Indices.Add(vertexRemap[v1]);
                    result.Indices.Add(vertexRemap[v2]);
                    result.Indices.Add(vertexRemap[v3]);
                }
            }

            return result;
        }
        private class MeshData
        {
            public int[] Indices { get; private set; }
            public bool[] IsTriangleValid { get; set; }
            public int TriangleCount { get; private set; }

            public MeshData(Mesh mesh)
            {
                Indices = mesh.Indices.ToArray();
                TriangleCount = Indices.Length / 3;
                IsTriangleValid = new bool[TriangleCount];

                // All triangles start as valid
                for (int i = 0; i < TriangleCount; i++)
                    IsTriangleValid[i] = true;
            }
        }

        private class VertexState
        {
            public Vector3 Position { get; set; }
            public SymmetricMatrix Quadric { get; set; }
            public bool IsRemoved { get; set; }
        }

        private class EdgeCollapse
        {
            public int VertexA { get; set; }
            public int VertexB { get; set; }
            public Vector3 TargetPosition { get; set; }
            public float Error { get; set; }
        }

       
        private void AddEdge(HashSet<Edge> edgeSet, Dictionary<int, List<Edge>> vertexEdges, int v1, int v2, int triIndex)
        {
            // Always store edges with smaller vertex index first
            if (v1 > v2)
            {
                int temp = v1;
                v1 = v2;
                v2 = temp;
            }

            var edge = new Edge(v1, v2);

            // If the edge is already in the set, update it with the additional triangle
            if (edgeSet.TryGetValue(edge, out Edge existingEdge))
            {
                existingEdge.TriangleIndices.Add(triIndex);
            }
            else
            {
                // Otherwise, add the new edge
                edge.TriangleIndices.Add(triIndex);
                edgeSet.Add(edge);
                vertexEdges[v1].Add(edge);
                vertexEdges[v2].Add(edge);
            }
        }
        

        // Helper classes and structures
        private class Edge
        {
            public int V1 { get; }
            public int V2 { get; }
            public List<int> TriangleIndices { get; set; }

            public Edge(int v1, int v2)
            {
                V1 = v1;
                V2 = v2;
                TriangleIndices = new List<int>();
            }
        }

        private class EdgeComparer : IEqualityComparer<Edge>
        {
            public bool Equals(Edge x, Edge y)
            {
                return x.V1 == y.V1 && x.V2 == y.V2;
            }

            public int GetHashCode(Edge obj)
            {
                return obj.V1 * 100000 + obj.V2;
            }
        }

        

        private class SymmetricMatrix
        {
            private float[] m = new float[10]; // Symmetric 4x4 matrix stored in lower triangular form

            public SymmetricMatrix()
            {
                for (int i = 0; i < 10; i++)
                    m[i] = 0;
            }

            public SymmetricMatrix(float a, float b, float c, float d, float e, float f, float g, float h)
            {
                m[0] = a * a; m[1] = a * b; m[2] = a * c; m[3] = a * d;
                m[4] = b * b; m[5] = b * c; m[6] = b * d;
                m[7] = c * c; m[8] = c * d;
                m[9] = d * d;
            }

            public SymmetricMatrix(SymmetricMatrix other)
            {
                for (int i = 0; i < 10; i++)
                    m[i] = other.m[i];
            }

            public void Add(SymmetricMatrix other)
            {
                for (int i = 0; i < 10; i++)
                    m[i] += other.m[i];
            }

            public float Evaluate(Vector3 v)
            {
                float x = v.X, y = v.Y, z = v.Z;
                return m[0] * x * x + 2 * m[1] * x * y + 2 * m[2] * x * z + 2 * m[3] * x +
                       m[4] * y * y + 2 * m[5] * y * z + 2 * m[6] * y +
                       m[7] * z * z + 2 * m[8] * z +
                       m[9];
            }

            public bool TryComputeOptimalPoint(out Vector3 point)
            {
                // For Qx=0 where Q is symmetric 3x3 matrix with bottom row/column = [0,0,0,1]
                float a = m[0], b = m[1], c = m[2], d = m[3];
                float e = m[4], f = m[5], g = m[6];
                float h = m[7], i = m[8];

                float det = a * (e * h - f * f) - b * (b * h - c * f) + c * (b * f - e * c);

                if (Math.Abs(det) < 1e-10) // Nearly singular matrix
                {
                    point = Vector3.Zero;
                    return false;
                }

                // Invert and solve
                float invDet = 1.0f / det;

                point = new Vector3(
                    (-d * (e * h - f * f) + g * (b * h - c * f) - i * (b * f - e * c)) * invDet,
                    (d * (b * h - c * f) - g * (a * h - c * c) + i * (a * f - b * c)) * invDet,
                    (-d * (b * f - e * c) + g * (a * f - b * c) - i * (a * e - b * b)) * invDet
                );

                return true;
            }
        }


        private Dictionary<int, double> CalculateVertexImportance(Mesh mesh)
        {
            Dictionary<int, double> importance = new Dictionary<int, double>();
            Dictionary<int, List<int>> vertexToTriangles = new Dictionary<int, List<int>>();
            List<Vector3> triangleNormals = new List<Vector3>();

            // Pre-calculate triangle normals and build vertex-to-triangle map
            for (int i = 0; i < mesh.Indices.Count / 3; i++)
            {
                int baseIdx = i * 3;
                int i1 = mesh.Indices[baseIdx];
                int i2 = mesh.Indices[baseIdx + 1];
                int i3 = mesh.Indices[baseIdx + 2];

                Vector3 v1 = mesh.Vertices[i1];
                Vector3 v2 = mesh.Vertices[i2];
                Vector3 v3 = mesh.Vertices[i3];

                // Calculate face normal
                Vector3 edge1 = v2 - v1;
                Vector3 edge2 = v3 - v1;
                Vector3 normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
                triangleNormals.Add(normal);

                // Add triangle to vertex-triangle mapping
                AddToVertexTriangleMap(vertexToTriangles, i1, i);
                AddToVertexTriangleMap(vertexToTriangles, i2, i);
                AddToVertexTriangleMap(vertexToTriangles, i3, i);
            }

            // Initialize importance for all vertices
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                importance[i] = 0.0;
            }

            // Calculate importance in parallel based on vertex adjacency
            Parallel.ForEach(vertexToTriangles, kvp =>
            {
                int vertexIdx = kvp.Key;
                List<int> triangles = kvp.Value;
                double vertexImportance = 0;

                // Compare each pair of triangles that share this vertex
                for (int i = 0; i < triangles.Count; i++)
                {
                    for (int j = i + 1; j < triangles.Count; j++)
                    {
                        int t1 = triangles[i];
                        int t2 = triangles[j];

                        // Calculate angle between normals
                        double angle = Math.Acos(MiscUtils.Clamp(
                            Vector3.Dot(triangleNormals[t1], triangleNormals[t2]), -1.0, 1.0));

                        vertexImportance += angle;
                    }
                }

                lock (importance)
                {
                    importance[vertexIdx] = vertexImportance;
                }
            });

            return importance;
        }

        private void AddToVertexTriangleMap(Dictionary<int, List<int>> map, int vertexIdx, int triangleIdx)
        {
            if (!map.TryGetValue(vertexIdx, out var triangles))
            {
                triangles = new List<int>();
                map[vertexIdx] = triangles;
            }
            triangles.Add(triangleIdx);
        }


        private void CalculateNormals(Mesh mesh)
        {
            // Initialize normals list
            mesh.Normals = new List<Vector3>(new Vector3[mesh.Vertices.Count]);

            // For each triangle
            for (int i = 0; i < mesh.Indices.Count; i += 3)
            {
                int a = mesh.Indices[i];
                int b = mesh.Indices[i + 1];
                int c = mesh.Indices[i + 2];

                Vector3 v1 = mesh.Vertices[a];
                Vector3 v2 = mesh.Vertices[b];
                Vector3 v3 = mesh.Vertices[c];

                // Calculate triangle normal
                Vector3 edge1 = v2 - v1;
                Vector3 edge2 = v3 - v1;
                Vector3 normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));

                // Add to vertex normals
                mesh.Normals[a] += normal;
                mesh.Normals[b] += normal;
                mesh.Normals[c] += normal;
            }

            // Normalize all normals
            for (int i = 0; i < mesh.Normals.Count; i++)
            {
                if (mesh.Normals[i] != Vector3.Zero)
                    mesh.Normals[i] = Vector3.Normalize(mesh.Normals[i]);
            }
        }

        // Helper class for triangle importance sorting
        private class TriangleInfo
        {
            public int Index1 { get; }
            public int Index2 { get; }
            public int Index3 { get; }
            public double Importance { get; }

            public TriangleInfo(int i1, int i2, int i3, double importance)
            {
                Index1 = i1;
                Index2 = i2;
                Index3 = i3;
                Importance = importance;
            }
        }
    }
}
