using CTSegmenter;
using System.Collections.Generic;

using System;

/// <summary>
/// A* pathfinding algorithm for finding optimal paths through volume material
/// </summary>
public class AStar
{
    // Inner class for nodes in the search graph
    private class Node
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public double GCost { get; set; } // Cost from start
        public double HCost { get; set; } // Heuristic cost to goal
        public double FCost => GCost + HCost; // Total cost
        public Node Parent { get; set; }

        public Node(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
            GCost = 0;
            HCost = 0;
            Parent = null;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is Node other))
                return false;
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override int GetHashCode()
        {
            return (X << 16) | (Y << 8) | Z;
        }
    }

    private readonly byte[,,] volumeLabels;
    private readonly byte materialID;
    private readonly int width;
    private readonly int height;
    private readonly int depth;

    public AStar(byte[,,] volumeLabels, byte materialID)
    {
        Logger.Log("[AStar] Initializing A* pathfinding algorithm");
        this.volumeLabels = volumeLabels;
        this.materialID = materialID;
        width = volumeLabels.GetLength(0);
        height = volumeLabels.GetLength(1);
        depth = volumeLabels.GetLength(2);
        Logger.Log($"[AStar] Volume size: {width} x {height} x {depth}");
        Logger.Log($"[AStar] Material ID: {materialID}");
        Logger.Log("[AStar] Initialization complete");
            }

    /// <summary>
    /// Finds a path through the material from start to goal
    /// </summary>
    /// <returns>List of points representing the path, or null if no path exists</returns>
    public List<AcousticSimulationForm.Point3D> FindPath(int startX, int startY, int startZ, int goalX, int goalY, int goalZ)
    {
        // Validate inputs
        if (startX < 0 || startX >= width || startY < 0 || startY >= height || startZ < 0 || startZ >= depth ||
            goalX < 0 || goalX >= width || goalY < 0 || goalY >= height || goalZ < 0 || goalZ >= depth)
        {
            Logger.Log("[AStar] Start or goal position out of bounds");
            return null;
        }

        // Ensure start and goal are in material
        if (volumeLabels[startX, startY, startZ] != materialID)
        {
            Logger.Log($"[AStar] Start position ({startX},{startY},{startZ}) is not in material {materialID}");
            return null;
        }

        if (volumeLabels[goalX, goalY, goalZ] != materialID)
        {
            Logger.Log($"[AStar] Goal position ({goalX},{goalY},{goalZ}) is not in material {materialID}");
            return null;
        }

        // Initialize open and closed sets
        var openSet = new List<Node>();
        var closedSet = new HashSet<Node>();
        var startNode = new Node(startX, startY, startZ);
        var goalNode = new Node(goalX, goalY, goalZ);

        // Start with the start node
        openSet.Add(startNode);

        // Process limit to prevent infinite search in complex volumes
        int maxIterations = width * height * depth / 10;
        int iterations = 0;

        while (openSet.Count > 0 && iterations < maxIterations)
        {
            iterations++;

            // Find the node with the lowest FCost
            Node current = openSet[0];
            for (int i = 1; i < openSet.Count; i++)
            {
                if (openSet[i].FCost < current.FCost ||
                    (openSet[i].FCost == current.FCost && openSet[i].HCost < current.HCost))
                {
                    current = openSet[i];
                }
            }

            // Move from open to closed set
            openSet.Remove(current);
            closedSet.Add(current);

            // Check if we've reached the goal
            if (current.X == goalNode.X && current.Y == goalNode.Y && current.Z == goalNode.Z)
            {
                Logger.Log($"[AStar] Path found in {iterations} iterations");
                return ReconstructPath(current);
            }

            // Process all neighbors
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        // Skip the current node
                        if (dx == 0 && dy == 0 && dz == 0)
                            continue;

                        // Calculate neighbor position
                        int nx = current.X + dx;
                        int ny = current.Y + dy;
                        int nz = current.Z + dz;

                        // Skip if out of bounds
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height || nz < 0 || nz >= depth)
                            continue;

                        // Skip if not in material
                        if (volumeLabels[nx, ny, nz] != materialID)
                            continue;

                        // Create the neighbor node
                        var neighbor = new Node(nx, ny, nz);

                        // Skip if already evaluated
                        bool inClosedSet = false;
                        foreach (var node in closedSet)
                        {
                            if (node.X == nx && node.Y == ny && node.Z == nz)
                            {
                                inClosedSet = true;
                                break;
                            }
                        }

                        if (inClosedSet)
                            continue;

                        // Calculate movement cost (Euclidean distance)
                        double movementCost = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        double gCost = current.GCost + movementCost;

                        // Check if the neighbor is in the open set
                        bool inOpenSet = false;
                        Node existingNode = null;
                        foreach (var node in openSet)
                        {
                            if (node.X == nx && node.Y == ny && node.Z == nz)
                            {
                                inOpenSet = true;
                                existingNode = node;
                                break;
                            }
                        }

                        // If not in open set or if this path is better
                        if (!inOpenSet || gCost < existingNode.GCost)
                        {
                            if (inOpenSet)
                            {
                                // Update existing node
                                existingNode.GCost = gCost;
                                existingNode.Parent = current;
                            }
                            else
                            {
                                // Add new node
                                neighbor.GCost = gCost;
                                neighbor.HCost = CalculateHeuristic(neighbor, goalNode);
                                neighbor.Parent = current;
                                openSet.Add(neighbor);
                            }
                        }
                    }
                }
            }
        }

        // No path found
        Logger.Log($"[AStar] No path found after {iterations} iterations");
        return null;
    }

    /// <summary>
    /// Calculates the heuristic cost (Euclidean distance) between two nodes
    /// </summary>
    private double CalculateHeuristic(Node a, Node b)
    {
        return Math.Sqrt(
            Math.Pow(a.X - b.X, 2) +
            Math.Pow(a.Y - b.Y, 2) +
            Math.Pow(a.Z - b.Z, 2));
    }

    /// <summary>
    /// Reconstructs the path from the goal node back to the start
    /// </summary>
    private List<AcousticSimulationForm.Point3D> ReconstructPath(Node endNode)
    {
        var path = new List<AcousticSimulationForm.Point3D>();
        Node current = endNode;

        while (current != null)
        {
            path.Add(new AcousticSimulationForm.Point3D(current.X, current.Y, current.Z));
            current = current.Parent;
        }

        // Path is from goal to start, so reverse it
        path.Reverse();

        // Simplify the path by removing redundant points
        if (path.Count > 3)
        {
            var simplifiedPath = new List<AcousticSimulationForm.Point3D>();
            simplifiedPath.Add(path[0]); // Always keep start

            for (int i = 1; i < path.Count - 1; i++)
            {
                // Only keep points that change direction
                bool keepPoint = true;

                // Check if three consecutive points are collinear
                if (i > 0 && i < path.Count - 1)
                {
                    AcousticSimulationForm.Point3D prev = path[i - 1];
                    AcousticSimulationForm.Point3D curr = path[i];
                    AcousticSimulationForm.Point3D next = path[i + 1];

                    // Check if we're moving in a straight line
                    int axesAligned = 0;
                    if (prev.X == curr.X && curr.X == next.X) axesAligned++;
                    if (prev.Y == curr.Y && curr.Y == next.Y) axesAligned++;
                    if (prev.Z == curr.Z && curr.Z == next.Z) axesAligned++;

                    // If moving in a straight line, skip this point
                    if (axesAligned >= 2)
                        keepPoint = false;
                }

                if (keepPoint)
                    simplifiedPath.Add(path[i]);
            }

            simplifiedPath.Add(path[path.Count - 1]); // Always keep end
            return simplifiedPath;
        }

        return path;
    }
}
