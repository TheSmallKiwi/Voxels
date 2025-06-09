using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Tuntenfisch.Extensions;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.Voxels;
using Unity.Mathematics;
using UnityEngine;

namespace Tuntenfisch.Fluids
{
    /// <summary>
    /// Asynchronous fluid simulation using UniTask and worker pooling pattern similar to DualContouring.
    /// Provides efficient async GPU operations with minimal overhead.
    /// </summary>
    [RequireComponent(typeof(VoxelConfig))]
    public class AsyncFluidSimulation : MonoBehaviour
    {
        private event Action OnDestroyed;

        [Header("Simulation Settings")] [SerializeField]
        private bool m_enableSimulation = true;

        [SerializeField] private float m_timeStep = 0.016f;
        [SerializeField] private int m_pressureIterations = 20;
        [SerializeField] private float m_viscosity = 0.01f;
        [SerializeField] private Vector3 m_gravity = new Vector3(0, -9.81f, 0);
        [SerializeField] private float m_densityDissipation = 0.99f;
        [SerializeField] private float m_velocityDissipation = 0.995f;

        [Header("Async Performance")] [Range(1, 8)] [SerializeField]
        private int m_numberOfWorkers = 4;

        [Min(0)] [SerializeField] private int m_initialTaskPoolPopulation = 0;
        [Range(1, 4)] [SerializeField] private int m_maxIterationsPerFrame = 2;
        [SerializeField] private float m_gpuTimeoutSeconds = 0.1f;

        [Header("Performance Monitoring")] [SerializeField]
        private bool m_enablePerformanceLogging = false;

        [SerializeField] private float m_performanceLogInterval = 5f;

        private VoxelConfig m_voxelConfig;
        private ComputeShader m_fluidCompute;

        // Worker system similar to DualContouring
        private Queue<Worker.Task> m_tasks;
        private Stack<Worker> m_availableWorkers;
        private Generics.Pool.ObjectPool<Worker.Task> m_taskPool;

        // Compute kernel IDs
        private int m_initializeFluidKernel;
        private int m_addSourcesKernel;
        private int m_advectionKernel;
        private int m_diffusionKernel;
        private int m_computeDivergenceKernel;
        private int m_pressureSolveKernel;
        private int m_pressureProjectionKernel;
        private int m_applyBoundariesKernel;
        private int m_updateBoundariesFromSolidsKernel;

        private bool m_initialized = false;
        private PerformanceTracker m_performanceTracker = new PerformanceTracker();

        public bool IsSimulationEnabled => m_enableSimulation && m_initialized;

        // Events for monitoring
        public Action<ChunkFluidData> OnSimulationCompleted;
        public Action<ChunkFluidData, string> OnSimulationError;

        private void Awake()
        {
            m_voxelConfig = GetComponent<VoxelConfig>();

            if (m_voxelConfig.FluidSimulationConfig == null)
            {
                Debug.LogError("AsyncFluidSimulation requires a FluidSimulationConfig in VoxelConfig!");
                enabled = false;
                return;
            }

            InitializeKernels();
            InitializeWorkerSystem();
            m_initialized = true;

            if (m_enablePerformanceLogging)
            {
                InvokeRepeating(nameof(LogPerformanceStats), m_performanceLogInterval, m_performanceLogInterval);
            }
        }

        private void InitializeKernels()
        {
            m_fluidCompute = m_voxelConfig.FluidSimulationConfig.Compute;

            if (m_fluidCompute == null)
            {
                Debug.LogError("Fluid compute shader not found in FluidSimulationConfig!");
                return;
            }

            // Find all kernel IDs
            m_initializeFluidKernel = m_fluidCompute.FindKernel("InitializeFluid");
            m_addSourcesKernel = m_fluidCompute.FindKernel("AddSources");
            m_advectionKernel = m_fluidCompute.FindKernel("Advection");
            m_diffusionKernel = m_fluidCompute.FindKernel("Diffusion");
            m_computeDivergenceKernel = m_fluidCompute.FindKernel("ComputeDivergence");
            m_pressureSolveKernel = m_fluidCompute.FindKernel("PressureSolve");
            m_pressureProjectionKernel = m_fluidCompute.FindKernel("PressureProjection");
            m_applyBoundariesKernel = m_fluidCompute.FindKernel("ApplyBoundaries");
            m_updateBoundariesFromSolidsKernel = m_fluidCompute.FindKernel("UpdateBoundariesFromSolids");
        }

        private void InitializeWorkerSystem()
        {
            m_tasks = new Queue<Worker.Task>();
            m_availableWorkers =
                new Stack<Worker>(Enumerable.Range(0, m_numberOfWorkers).Select(index => new Worker(this)));
            m_taskPool =
                new Generics.Pool.ObjectPool<Worker.Task>(() => new Worker.Task(), m_initialTaskPoolPopulation);
        }

        private void LateUpdate()
        {
            // Dispatch workers similar to DualContouring pattern
            while (m_tasks.Count > 0 && m_availableWorkers.Count > 0)
            {
                DispatchWorker(m_tasks.Dequeue());
            }

            m_performanceTracker.Update();
        }

        private void OnDestroy() => OnDestroyed?.Invoke();

        private void OnValidate()
        {
            if (m_availableWorkers == null)
                return;

            // Recreate worker pool when settings change
            foreach (Worker worker in m_availableWorkers)
            {
                worker.Dispose();
            }

            m_availableWorkers =
                new Stack<Worker>(Enumerable.Range(0, m_numberOfWorkers).Select(index => new Worker(this)));
        }

        /// <summary>
        /// Request asynchronous simulation for a chunk - main public API
        /// </summary>
        public IRequest RequestSimulationAsync(ChunkFluidData fluidData, SimulationCallback callback)
        {
            if (!IsSimulationEnabled || !fluidData.IsValid())
            {
                callback?.Invoke(false);
                return null;
            }

            Worker.Task task = m_taskPool.Acquire();
            task.FluidData = fluidData ?? throw new ArgumentNullException(nameof(fluidData));
            task.Callback = callback;
            task.WorldPosition = fluidData.WorldPosition;

            // If a worker is available, directly dispatch the task
            if (m_availableWorkers.Count > 0)
            {
                DispatchWorker(task);
            }
            else
            {
                m_tasks.Enqueue(task);
            }

            m_performanceTracker.QueuedTasks++;
            return task;
        }

        /// <summary>
        /// Initialize chunk fluid textures asynchronously
        /// </summary>
        public IRequest InitializeChunkAsync(ChunkFluidData fluidData, System.Action onComplete = null)
        {
            InitializeFluidTexturesImmediate(fluidData);
            return null;
            // return RequestSimulationAsync(fluidData, (success) =>
            // {
            //     if (success)
            //     {
            //         
            //     }
            //
            //     onComplete?.Invoke();
            // });
        }

        /// <summary>
        /// Update boundaries from solids asynchronously
        /// </summary>
        public IRequest UpdateBoundariesAsync(ChunkFluidData fluidData, System.Action onComplete = null)
        {
            UpdateBoundariesFromSolidsImmediate(fluidData);
            return null;
            // return RequestSimulationAsync(fluidData, (success) =>
            // {
            //     if (success)
            //     {
            //         
            //     }
            //
            //     onComplete?.Invoke();
            // });
        }

        private void DispatchWorker(Worker.Task task)
        {
            if (task.Canceled)
            {
                m_taskPool.Release(task);
                return;
            }

            DispatchWorkerUniTask(task).Forget();
        }

        private async UniTaskVoid DispatchWorkerUniTask(Worker.Task task)
        {
            Worker worker = m_availableWorkers.Pop();
            worker.StartSimulation(task);

            var cancellationToken = this.GetCancellationTokenOnDestroy();

            do
            {
                await UniTask.NextFrame(cancellationToken);
            } while (worker.IsProcessing() && !cancellationToken.IsCancellationRequested);

            // Call callback if task hasn't been canceled
            bool success = worker.GetResult();
            if (!task.Canceled && !cancellationToken.IsCancellationRequested)
            {
                task.Callback?.Invoke(success);

                if (success)
                {
                    OnSimulationCompleted?.Invoke(task.FluidData);
                    m_performanceTracker.CompletedTasks++;
                }
                else
                {
                    OnSimulationError?.Invoke(task.FluidData, "Simulation failed");
                    m_performanceTracker.FailedTasks++;
                }
            }

            m_taskPool.Release(task);

            // Return worker to pool or dispose if we have too many
            if (m_availableWorkers.Count < m_numberOfWorkers)
            {
                m_availableWorkers.Push(worker);
            }
            else
            {
                worker.Dispose();
            }
        }

        private void InitializeFluidTexturesImmediate(ChunkFluidData fluidData)
        {
            SetGlobalParameters(fluidData.WorldPosition);
            BindTexturesForKernel(m_initializeFluidKernel, fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);
            m_fluidCompute.Dispatch(m_initializeFluidKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
        }

        private void UpdateBoundariesFromSolidsImmediate(ChunkFluidData fluidData)
        {
            SetGlobalParameters(fluidData.WorldPosition);
            BindTexturesForKernel(m_updateBoundariesFromSolidsKernel, fluidData.FluidTextures,
                fluidData.SolidVoxelBuffer, true);
            m_fluidCompute.Dispatch(m_updateBoundariesFromSolidsKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
        }

        private void BindTexturesForKernel(int kernelId, ChunkFluidTextures fluidData,
            ComputeBuffer solidVoxelBuffer, bool bindWriteTextures)
        {
            // Bind read textures
            m_fluidCompute.SetTexture(kernelId, "velocityRead", fluidData.VelocityRead);
            m_fluidCompute.SetTexture(kernelId, "densityRead", fluidData.DensityRead);
            m_fluidCompute.SetTexture(kernelId, "pressureRead", fluidData.PressureRead);

            if (bindWriteTextures)
            {
                // Bind write textures
                m_fluidCompute.SetTexture(kernelId, "velocityWrite", fluidData.VelocityWrite);
                m_fluidCompute.SetTexture(kernelId, "densityWrite", fluidData.DensityWrite);
                m_fluidCompute.SetTexture(kernelId, "pressureWrite", fluidData.PressureWrite);
            }

            // Bind divergence texture (always writable)
            m_fluidCompute.SetTexture(kernelId, "divergence", fluidData.Divergence);

            // Bind solid voxel buffer if provided
            if (solidVoxelBuffer != null)
            {
                m_fluidCompute.SetBuffer(kernelId, "solidVoxelVolume", solidVoxelBuffer);
            }
        }

        private void SetGlobalParameters(float3 chunkWorldPosition)
        {
            var volumeConfig = m_voxelConfig.VoxelVolumeConfig;

            // Simulation parameters
            m_fluidCompute.SetFloat("deltaTime", m_timeStep);
            m_fluidCompute.SetFloat("viscosity", m_viscosity);
            m_fluidCompute.SetVector("gravity", m_gravity);
            m_fluidCompute.SetFloat("densityDissipation", m_densityDissipation);
            m_fluidCompute.SetFloat("velocityDissipation", m_velocityDissipation);

            // Volume parameters
            m_fluidCompute.SetInts("dimensions", volumeConfig.NumberOfVoxels.x,
                volumeConfig.NumberOfVoxels.y, volumeConfig.NumberOfVoxels.z);
            m_fluidCompute.SetFloat("voxelSize", volumeConfig.VoxelSpacing);
        }

        private void SetFluidSourceParameters(FluidSourceData sourceData)
        {
            m_fluidCompute.SetVector("sourcePosition", sourceData.Position);
            m_fluidCompute.SetVector("sourceVelocity", sourceData.Velocity);
            m_fluidCompute.SetFloat("sourceRadius", sourceData.Radius);
            m_fluidCompute.SetFloat("sourceDensity", sourceData.Amount);
        }

        private void LogPerformanceStats()
        {
            if (!m_enablePerformanceLogging)
                return;

            var stats = m_performanceTracker.GetStats();
            Debug.Log(
                $"[AsyncFluidSim] Queue: {m_tasks.Count}, Workers: {m_numberOfWorkers - m_availableWorkers.Count}/{m_numberOfWorkers}, " +
                $"Completed: {stats.CompletedTasks}, Failed: {stats.FailedTasks}, " +
                $"Avg Time: {stats.AverageSimulationTime:F3}s");
        }

        public PerformanceStats GetPerformanceStats() => m_performanceTracker.GetStats();
        public int GetQueueLength() => m_tasks.Count;
        public int GetActiveWorkerCount() => m_numberOfWorkers - m_availableWorkers.Count;

        private class Worker : IDisposable
        {
            private AsyncFluidSimulation m_parent;
            private Task m_currentTask;
            private bool m_isProcessing;
            private bool m_simulationResult;
            private float m_startTime;

            public Worker(AsyncFluidSimulation parent)
            {
                m_parent = parent;
                m_parent.OnDestroyed += Dispose;
            }

            public void Dispose()
            {
                m_parent.OnDestroyed -= Dispose;
                m_parent = null;
                m_currentTask = null;
                m_isProcessing = false;
            }

            public void StartSimulation(Task task)
            {
                m_currentTask = task;
                m_isProcessing = true;
                m_simulationResult = false;
                m_startTime = Time.realtimeSinceStartup;

                ProcessSimulationAsync().Forget();
            }

            public bool IsProcessing() => m_isProcessing;
            public bool GetResult() => m_simulationResult;

            private async UniTaskVoid ProcessSimulationAsync()
            {
                try
                {
                    var fluidData = m_currentTask.FluidData;
                    var cancellationToken = m_parent.GetCancellationTokenOnDestroy();

                    // Set parameters for this chunk
                    m_parent.SetGlobalParameters(fluidData.WorldPosition);

                    // Execute simulation pipeline
                    await ExecuteSimulationPipeline(fluidData, cancellationToken);
                    
                    // Debug.Log("Swapped Textures");

                    m_simulationResult = true;

                    // Record performance
                    float totalTime = Time.realtimeSinceStartup - m_startTime;
                    m_parent.m_performanceTracker.RecordSimulationTime(totalTime);
                }
                catch (OperationCanceledException)
                {
                    // Simulation was canceled
                    m_simulationResult = false;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Fluid simulation failed: {e.Message}");
                    m_simulationResult = false;
                }
                finally
                {
                    m_isProcessing = false;
                }
            }

            private async UniTask ExecuteSimulationPipeline(ChunkFluidData fluidData,
                System.Threading.CancellationToken cancellationToken)
            {
                var numberOfVoxels = m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels;

                // 1. Update boundaries from solid geometry
                await ExecuteComputeStepAsync("UpdateBoundaries", () =>
                {
                    m_parent.BindTexturesForKernel(m_parent.m_updateBoundariesFromSolidsKernel,
                        fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);
                    m_parent.m_fluidCompute.Dispatch(m_parent.m_updateBoundariesFromSolidsKernel, numberOfVoxels);
                    fluidData.FluidTextures.SwapTextures(); // Swap after boundaries are updated
                }, cancellationToken);
                
                // Debug.Log("UpdatedBoundaries");

                // 2. Add fluid sources if present
                if (fluidData.FluidSource != null && fluidData.FluidSource.ShouldBeActive())
                {
                    await ExecuteComputeStepAsync("AddSources", () =>
                    {
                        m_parent.SetFluidSourceParameters(fluidData.FluidSource);
                        m_parent.BindTexturesForKernel(m_parent.m_addSourcesKernel,
                            fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);
                        m_parent.m_fluidCompute.Dispatch(m_parent.m_addSourcesKernel, numberOfVoxels);
                        fluidData.FluidTextures.SwapDensityTextures(); // Swap after sources are added
                        fluidData.FluidTextures.SwapVelocityTextures(); 
                    }, cancellationToken);
                }
                
                // Debug.Log("AddedSources");

                // 3. Advection step
                await ExecuteComputeStepAsync("Advection", () =>
                {
                    m_parent.BindTexturesForKernel(m_parent.m_advectionKernel,
                        fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);
                    m_parent.m_fluidCompute.Dispatch(m_parent.m_advectionKernel, numberOfVoxels);
                    fluidData.FluidTextures.SwapDensityTextures(); // Swap after sources are added
                    fluidData.FluidTextures.SwapVelocityTextures(); 
                }, cancellationToken);
                
                // Debug.Log("Advected");

                // 4. Diffusion (viscosity) - optional
                if (m_parent.m_viscosity > 0.001f)
                {
                    await ExecuteComputeStepAsync("Diffusion", () =>
                    {
                        m_parent.BindTexturesForKernel(m_parent.m_diffusionKernel,
                            fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);
                        m_parent.m_fluidCompute.Dispatch(m_parent.m_diffusionKernel, numberOfVoxels);
                        fluidData.FluidTextures.SwapDensityTextures(); // Swap after diffusion
                        fluidData.FluidTextures.SwapVelocityTextures(); 
                    }, cancellationToken);
                }
                
                // Debug.Log("Diffused");

                // 5. Pressure projection (incompressibility)
                await ExecutePressureProjectionAsync(fluidData, numberOfVoxels, cancellationToken);

                // 6. Apply boundary conditions
                await ExecuteComputeStepAsync("ApplyBoundaries", () =>
                {
                    m_parent.BindTexturesForKernel(m_parent.m_applyBoundariesKernel,
                        fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);
                    m_parent.m_fluidCompute.Dispatch(m_parent.m_applyBoundariesKernel, numberOfVoxels);
                }, cancellationToken);
            }

            private async UniTask ExecutePressureProjectionAsync(ChunkFluidData fluidData, int3 numberOfVoxels,
                System.Threading.CancellationToken cancellationToken)
            {
                var textures = fluidData.FluidTextures;

                // Compute divergence
                await ExecuteComputeStepAsync("ComputeDivergence", () =>
                {
                    m_parent.BindTexturesForKernel(m_parent.m_computeDivergenceKernel,
                        textures, fluidData.SolidVoxelBuffer, false);
                    m_parent.m_fluidCompute.Dispatch(m_parent.m_computeDivergenceKernel, numberOfVoxels);
                    // No swap
                }, cancellationToken);
                
                // Debug.Log("ComputedDivergence");

                // Iterative pressure solve - spread across frames if needed
                int iterationsPerFrame = math.max(1, m_parent.m_pressureIterations / m_parent.m_maxIterationsPerFrame);

                for (int i = 0; i < m_parent.m_pressureIterations; i += iterationsPerFrame)
                {
                    int iterationsThisFrame = math.min(iterationsPerFrame, m_parent.m_pressureIterations - i);

                    await ExecuteComputeStepAsync($"PressureSolve_{i}", () =>
                    {
                        for (int j = 0; j < iterationsThisFrame; j++)
                        {
                            m_parent.BindTexturesForKernel(m_parent.m_pressureSolveKernel,
                                textures, fluidData.SolidVoxelBuffer, true);
                            m_parent.m_fluidCompute.Dispatch(m_parent.m_pressureSolveKernel, numberOfVoxels);
                            textures.SwapPressureTextures(); // Only swap pressure textures
                        }
                    }, cancellationToken);
                }
                
                // Debug.Log("PressureSolved");

                // Apply pressure gradient to velocity
                await ExecuteComputeStepAsync("PressureProjection", () =>
                {
                    m_parent.BindTexturesForKernel(m_parent.m_pressureProjectionKernel,
                        textures, fluidData.SolidVoxelBuffer, true);
                    m_parent.m_fluidCompute.Dispatch(m_parent.m_pressureProjectionKernel, numberOfVoxels);
                    textures.SwapVelocityTextures(); // Only swap velocity textures
                }, cancellationToken);
                
                // Debug.Log("PressureProjected");
            }

            private async UniTask ExecuteComputeStepAsync(string stepName, System.Action computeAction,
                System.Threading.CancellationToken cancellationToken)
            {
                float stepStartTime = Time.realtimeSinceStartup;

                computeAction?.Invoke();
                await UniTask.NextFrame(cancellationToken);

                // Wait for GPU completion using async fence - Not supported on DX11
                // var fence = Graphics.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation,
                //     SynchronisationStageFlags.ComputeProcessing);
                // float timeoutTime = Time.realtimeSinceStartup + m_parent.m_gpuTimeoutSeconds;

                // while (!fence.passed && Time.realtimeSinceStartup < timeoutTime &&
                //        !cancellationToken.IsCancellationRequested)
                // {
                //     await UniTask.NextFrame(cancellationToken);
                // }
                //
                // if (!fence.passed && !cancellationToken.IsCancellationRequested)
                // {
                //     Debug.LogWarning($"GPU fence timeout for step: {stepName}");
                // }

                // Record step performance
                float stepDuration = Time.realtimeSinceStartup - stepStartTime;
                m_parent.m_performanceTracker.RecordStepTime(stepName, stepDuration);
            }

            public class Task : IPoolable, IRequest
            {
                public bool Canceled { get; private set; }
                public ChunkFluidData FluidData { get; set; }
                public float3 WorldPosition { get; set; }
                public SimulationCallback Callback { get; set; }

                public void OnAcquire()
                {
                    Canceled = false;
                }

                public void OnRelease()
                {
                    FluidData = null;
                    Callback = null;
                    Canceled = false;
                }

                public void Cancel() => Canceled = true;
            }
        }
    }

    // Supporting types
    public delegate void SimulationCallback(bool success);

    public struct PerformanceStats
    {
        public int QueuedTasks;
        public int CompletedTasks;
        public int FailedTasks;
        public float AverageSimulationTime;
        public Dictionary<string, float> StepTimes;
    }

    public class PerformanceTracker
    {
        public int QueuedTasks;
        public int CompletedTasks;
        public int FailedTasks;

        private List<float> m_simulationTimes = new List<float>();
        private Dictionary<string, List<float>> m_stepTimes = new Dictionary<string, List<float>>();
        private const int MAX_HISTORY = 50;

        public void Update()
        {
            // Cleanup old performance data
            if (m_simulationTimes.Count > MAX_HISTORY)
            {
                m_simulationTimes.RemoveRange(0, m_simulationTimes.Count - MAX_HISTORY);
            }
        }

        public void RecordSimulationTime(float time)
        {
            m_simulationTimes.Add(time);
        }

        public void RecordStepTime(string stepName, float time)
        {
            if (!m_stepTimes.ContainsKey(stepName))
            {
                m_stepTimes[stepName] = new List<float>();
            }

            m_stepTimes[stepName].Add(time);

            if (m_stepTimes[stepName].Count > MAX_HISTORY)
            {
                m_stepTimes[stepName].RemoveAt(0);
            }
        }

        public PerformanceStats GetStats()
        {
            var stats = new PerformanceStats
            {
                QueuedTasks = QueuedTasks,
                CompletedTasks = CompletedTasks,
                FailedTasks = FailedTasks,
                StepTimes = new Dictionary<string, float>()
            };

            // Calculate average simulation time
            if (m_simulationTimes.Count > 0)
            {
                float total = 0;
                foreach (float time in m_simulationTimes)
                {
                    total += time;
                }

                stats.AverageSimulationTime = total / m_simulationTimes.Count;
            }

            // Calculate average step times
            foreach (var kvp in m_stepTimes)
            {
                if (kvp.Value.Count > 0)
                {
                    float total = 0;
                    foreach (float time in kvp.Value)
                    {
                        total += time;
                    }

                    stats.StepTimes[kvp.Key] = total / kvp.Value.Count;
                }
            }

            return stats;
        }
    }
}