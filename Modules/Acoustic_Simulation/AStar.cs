using CTS;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;

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

        // Early termination for very close points
        double directDistance = Math.Sqrt(
            Math.Pow(goalX - startX, 2) +
            Math.Pow(goalY - startY, 2) +
            Math.Pow(goalZ - startZ, 2));

        if (directDistance < 5) // If less than 5 voxels apart, use direct path
        {
            Logger.Log("[AStar] Start and goal are very close, using direct path");
            return CreateDirectPath(startX, startY, startZ, goalX, goalY, goalZ);
        }

        // Initialize open and closed sets
        var openSet = new List<Node>();
        var closedSet = new HashSet<string>(); // Use string keys for faster lookup
        var startNode = new Node(startX, startY, startZ);
        var goalNode = new Node(goalX, goalY, goalZ);

        // Dictionary for quick lookup of nodes in open set
        var openSetLookup = new Dictionary<string, Node>();

        // Start with the start node
        startNode.HCost = CalculateHeuristic(startNode, goalNode);
        openSet.Add(startNode);
        openSetLookup.Add(NodeToKey(startNode), startNode);

        // Process limit to prevent infinite search in complex volumes
        int maxIterations = width * height * depth / 5; // More generous limit
        int iterations = 0;

        while (openSet.Count > 0 && iterations < maxIterations)
        {
            iterations++;

            // Find the node with the lowest FCost more efficiently
            Node current = GetLowestFCostNode(openSet);
            string currentKey = NodeToKey(current);

            // Move from open to closed set
            openSet.Remove(current);
            openSetLookup.Remove(currentKey);
            closedSet.Add(currentKey);

            // Check if we've reached the goal
            if (current.X == goalNode.X && current.Y == goalNode.Y && current.Z == goalNode.Z)
            {
                Logger.Log($"[AStar] Path found in {iterations} iterations");
                return ReconstructPath(current);
            }

            // Process all neighbors more efficiently
            ProcessNeighbors(current, goalNode, openSet, openSetLookup, closedSet);
        }

        // No path found, create best possible path
        Logger.Log($"[AStar] No path found after {iterations} iterations");
        return FindBestPartialPath(startX, startY, startZ, goalX, goalY, goalZ);
    }

    /// <summary>
    /// Finds the node with the lowest FCost in the open set
    /// </summary>
    private Node GetLowestFCostNode(List<Node> openSet)
    {
        Node lowest = openSet[0];
        for (int i = 1; i < openSet.Count; i++)
        {
            if (openSet[i].FCost < lowest.FCost ||
                (openSet[i].FCost == lowest.FCost && openSet[i].HCost < lowest.HCost))
            {
                lowest = openSet[i];
            }
        }
        return lowest;
    }

    /// <summary>
    /// Converts a node to a string key for dictionary/set lookup
    /// </summary>
    private string NodeToKey(Node node)
    {
        return $"{node.X},{node.Y},{node.Z}";
    }

    /// <summary>
    /// Creates a direct path between two points
    /// </summary>
    private List<AcousticSimulationForm.Point3D> CreateDirectPath(int startX, int startY, int startZ, int goalX, int goalY, int goalZ)
    {
        var path = new List<AcousticSimulationForm.Point3D>();

        // Calculate the number of steps based on the longest dimension difference
        int steps = Math.Max(Math.Max(Math.Abs(goalX - startX), Math.Abs(goalY - startY)), Math.Abs(goalZ - startZ));
        steps = Math.Max(steps, 1); // Ensure at least one step

        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = (int)Math.Round(startX + (goalX - startX) * t);
            int y = (int)Math.Round(startY + (goalY - startY) * t);
            int z = (int)Math.Round(startZ + (goalZ - startZ) * t);

            path.Add(new AcousticSimulationForm.Point3D(x, y, z));
        }

        return path;
    }

    /// <summary>
    /// Process neighbors of the current node more efficiently
    /// </summary>
    private void ProcessNeighbors(Node current, Node goalNode, List<Node> openSet,
                                 Dictionary<string, Node> openSetLookup, HashSet<string> closedSet)
    {
        // Use 6-connectivity (only direct neighbors) for better performance
        int[] dx = { 1, -1, 0, 0, 0, 0 };
        int[] dy = { 0, 0, 1, -1, 0, 0 };
        int[] dz = { 0, 0, 0, 0, 1, -1 };

        for (int i = 0; i < 6; i++)
        {
            int nx = current.X + dx[i];
            int ny = current.Y + dy[i];
            int nz = current.Z + dz[i];

            // Skip if out of bounds
            if (nx < 0 || nx >= width || ny < 0 || ny >= height || nz < 0 || nz >= depth)
                continue;

            // Skip if not in material
            if (volumeLabels[nx, ny, nz] != materialID)
                continue;

            // Skip if already evaluated (in closed set)
            string neighborKey = $"{nx},{ny},{nz}";
            if (closedSet.Contains(neighborKey))
                continue;

            // Calculate movement cost (euclidean distance)
            double movementCost = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i] + dz[i] * dz[i]);
            double gCost = current.GCost + movementCost;

            // Check if the neighbor is in the open set
            bool inOpenSet = openSetLookup.TryGetValue(neighborKey, out Node existingNode);

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
                    Node neighbor = new Node(nx, ny, nz);
                    neighbor.GCost = gCost;
                    neighbor.HCost = CalculateHeuristic(neighbor, goalNode);
                    neighbor.Parent = current;
                    openSet.Add(neighbor);
                    openSetLookup.Add(neighborKey, neighbor);
                }
            }
        }
    }

    /// <summary>
    /// Find the best path even when a complete path can't be found
    /// </summary>
    private List<AcousticSimulationForm.Point3D> FindBestPartialPath(int startX, int startY, int startZ, int goalX, int goalY, int goalZ)
    {
        Logger.Log("[AStar] Attempting to find best partial path");

        // Determine best axis to follow based on the start/goal positions
        int dx = Math.Abs(goalX - startX);
        int dy = Math.Abs(goalY - startY);
        int dz = Math.Abs(goalZ - startZ);

        List<AcousticSimulationForm.Point3D> path;

        // Try to path along the dominant axis
        if (dx >= dy && dx >= dz)
        {
            path = TryPathAlongAxis(startX, startY, startZ, goalX, goalY, goalZ, 0);
        }
        else if (dy >= dx && dy >= dz)
        {
            path = TryPathAlongAxis(startX, startY, startZ, goalX, goalY, goalZ, 1);
        }
        else
        {
            path = TryPathAlongAxis(startX, startY, startZ, goalX, goalY, goalZ, 2);
        }

        if (path.Count > 1)
        {
            Logger.Log($"[AStar] Created partial path with {path.Count} points");
            return path;
        }

        // Last resort: straight line path
        Logger.Log("[AStar] Falling back to straight line path");
        return CreateDirectPath(startX, startY, startZ, goalX, goalY, goalZ);
    }

    /// <summary>
    /// Try to find a path following a specific axis
    /// </summary>
    private List<AcousticSimulationForm.Point3D> TryPathAlongAxis(int startX, int startY, int startZ,
                                                                 int goalX, int goalY, int goalZ, int axis)
    {
        var path = new List<AcousticSimulationForm.Point3D>();
        path.Add(new AcousticSimulationForm.Point3D(startX, startY, startZ));

        int currentX = startX;
        int currentY = startY;
        int currentZ = startZ;

        // Determine the direction to move along the selected axis
        int stepX = Math.Sign(goalX - startX);
        int stepY = Math.Sign(goalY - startY);
        int stepZ = Math.Sign(goalZ - startZ);

        // Ensure we have a step in the primary axis
        if (axis == 0 && stepX == 0) stepX = 1;
        if (axis == 1 && stepY == 0) stepY = 1;
        if (axis == 2 && stepZ == 0) stepZ = 1;

        int maxSteps = Math.Max(Math.Max(width, height), depth);

        for (int i = 0; i < maxSteps; i++)
        {
            int nextX = currentX;
            int nextY = currentY;
            int nextZ = currentZ;

            // Try to move primarily along the selected axis
            switch (axis)
            {
                case 0: nextX += stepX; break; // X-axis
                case 1: nextY += stepY; break; // Y-axis
                case 2: nextZ += stepZ; break; // Z-axis
            }

            // Check if the proposed position is valid
            if (nextX >= 0 && nextX < width && nextY >= 0 && nextY < height && nextZ >= 0 && nextZ < depth)
            {
                // Check if this position is in the material
                if (volumeLabels[nextX, nextY, nextZ] == materialID)
                {
                    currentX = nextX;
                    currentY = nextY;
                    currentZ = nextZ;
                    path.Add(new AcousticSimulationForm.Point3D(currentX, currentY, currentZ));
                }
                else
                {
                    // Search for nearby material point
                    bool found = false;
                    for (int r = 1; r <= 5 && !found; r++) // Search radius
                    {
                        // Search only in the plane perpendicular to our axis
                        for (int d1 = -r; d1 <= r && !found; d1++)
                        {
                            for (int d2 = -r; d2 <= r && !found; d2++)
                            {
                                int nx = nextX, ny = nextY, nz = nextZ;

                                // Apply offset based on our axis
                                switch (axis)
                                {
                                    case 0: // X-axis - adjust Y,Z
                                        ny = nextY + d1;
                                        nz = nextZ + d2;
                                        break;
                                    case 1: // Y-axis - adjust X,Z
                                        nx = nextX + d1;
                                        nz = nextZ + d2;
                                        break;
                                    case 2: // Z-axis - adjust X,Y
                                        nx = nextX + d1;
                                        ny = nextY + d2;
                                        break;
                                }

                                if (nx >= 0 && nx < width && ny >= 0 && ny < height && nz >= 0 && nz < depth)
                                {
                                    if (volumeLabels[nx, ny, nz] == materialID)
                                    {
                                        currentX = nx;
                                        currentY = ny;
                                        currentZ = nz;
                                        path.Add(new AcousticSimulationForm.Point3D(currentX, currentY, currentZ));
                                        found = true;
                                    }
                                }
                            }
                        }
                    }

                    if (!found)
                    {
                        // If no nearby point, try skipping ahead along our axis
                        int skipDistance = 5;
                        switch (axis)
                        {
                            case 0: nextX += stepX * skipDistance; break;
                            case 1: nextY += stepY * skipDistance; break;
                            case 2: nextZ += stepZ * skipDistance; break;
                        }

                        if (nextX >= 0 && nextX < width && nextY >= 0 && nextY < height && nextZ >= 0 && nextZ < depth &&
                            volumeLabels[nextX, nextY, nextZ] == materialID)
                        {
                            currentX = nextX;
                            currentY = nextY;
                            currentZ = nextZ;
                            path.Add(new AcousticSimulationForm.Point3D(currentX, currentY, currentZ));
                        }
                        else
                        {
                            // Can't proceed further
                            break;
                        }
                    }
                }
            }
            else
            {
                // Out of bounds
                break;
            }

            // Check if we're close to the goal
            double distToGoal = Math.Sqrt(
                Math.Pow(currentX - goalX, 2) +
                Math.Pow(currentY - goalY, 2) +
                Math.Pow(currentZ - goalZ, 2));

            if (distToGoal < 3) // Close enough to goal
            {
                path.Add(new AcousticSimulationForm.Point3D(goalX, goalY, goalZ));
                break;
            }
        }

        // Ensure the path ends at the goal
        if (path.Count > 0 &&
            (path[path.Count - 1].X != goalX || path[path.Count - 1].Y != goalY || path[path.Count - 1].Z != goalZ))
        {
            path.Add(new AcousticSimulationForm.Point3D(goalX, goalY, goalZ));
        }

        return path;
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