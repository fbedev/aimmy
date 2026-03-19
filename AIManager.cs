using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Aimmy.Mac
{
    public class AIManager : IDisposable
    {
        private InferenceSession? _onnxModel;
        private RunOptions? _modelOptions;
        private List<string>? _outputNames;
        
        private int NUM_DETECTIONS = 8400;
        public int IMAGE_SIZE { get; private set; } = 640;
        
        private Dictionary<int, string> _modelClasses = new Dictionary<int, string> { { 0, "enemy" } };

        private DenseTensor<float>? _reusableTensor;
        private float[]? _reusableInputArray;
        private List<NamedOnnxValue>? _reusableInputs;

        public bool IsLoaded => _onnxModel != null;

        public AIManager(string modelPath)
        {
            try
            {
                var sessionOptions = new SessionOptions
                {
                    EnableCpuMemArena = true,
                    EnableMemoryPattern = false,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                
                // M4 Pro CPU is strong, but CoreML is stronger
                sessionOptions.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_ENABLE_ON_SUBGRAPH);

                _modelOptions = new RunOptions();
                _onnxModel = new InferenceSession(modelPath, sessionOptions);
                _outputNames = new List<string>(_onnxModel.OutputMetadata.Keys);

                ValidateModel();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading model: {ex.Message}");
            }
        }

        private void ValidateModel()
        {
            if (_onnxModel == null) return;

            var inputMeta = _onnxModel.InputMetadata;
            foreach (var input in inputMeta)
            {
                var dims = input.Value.Dimensions;
                if (dims.Length == 4 && dims[2] > 0)
                {
                    IMAGE_SIZE = dims[2];
                }
            }
             
             NUM_DETECTIONS = MathUtil.CalculateNumDetections(IMAGE_SIZE);
             
             // Initialize buffers
             _reusableInputArray = new float[3 * IMAGE_SIZE * IMAGE_SIZE];
             _reusableTensor = new DenseTensor<float>(_reusableInputArray, new int[] { 1, 3, IMAGE_SIZE, IMAGE_SIZE });
             _reusableInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", _reusableTensor) };
        }

        // --- Realtime API ---
        
        public float[]? GetInputBuffer() => _reusableInputArray;

        public List<Prediction> PredictFromBuffer(float minConfidence = 0.5f)
        {
             if (_onnxModel == null || _reusableInputs == null) return new List<Prediction>();

             using (var results = _onnxModel.Run(_reusableInputs, _outputNames, _modelOptions))
             {
                var outputTensor = results[0].AsTensor<float>(); 
                return ProcessResults(outputTensor, minConfidence);
             }
        }

        // --- File API ---

        public List<Prediction> Predict(string imagePath, float minConfidence = 0.5f)
        {
            if (!File.Exists(imagePath)) return new List<Prediction>();

            using (var image = Image.Load<Rgb24>(imagePath))
            {
                if (image.Width != IMAGE_SIZE || image.Height != IMAGE_SIZE)
                {
                    image.Mutate(x => x.Resize(IMAGE_SIZE, IMAGE_SIZE));
                }
                
                if (_reusableInputArray == null) return new List<Prediction>();
                MathUtil.ImageToFloatArray(image, _reusableInputArray);
                
                return PredictFromBuffer(minConfidence);
            }
        }

        private List<Prediction> ProcessResults(Tensor<float> outputTensor, float minConfidence)
        {
             var predictions = new List<Prediction>();

             // Calculate NMS or just raw?
             // Windows version handles NMS inside? Let's verify Windows logic later. 
             // For now, let's return raw detections that pass confidence, and let Program.cs sort?
             // Or simple loop logic.
             
             // NOTE: YOLOv8 output is [1, 4+Classes, 8400].
             // We iterate 8400 columns.
             
             for (int i = 0; i < NUM_DETECTIONS; i++)
             {
                 float confidence = outputTensor[0, 4, i]; 
                 
                 if (confidence > minConfidence)
                 {
                     float x_center = outputTensor[0, 0, i];
                     float y_center = outputTensor[0, 1, i];
                     float width = outputTensor[0, 2, i];
                     float height = outputTensor[0, 3, i];
                     
                     float x_min = x_center - width / 2;
                     float y_min = y_center - height / 2;

                     predictions.Add(new Prediction
                     {
                         Confidence = confidence,
                         Rectangle = new RectF(x_min, y_min, width, height),
                         ClassName = "Enemy",
                         ClassId = 0,
                         ScreenCenterX = x_center, 
                         ScreenCenterY = y_center
                     });
                 }
             }
             
             // Basic NMS could go here, but let's let multiple boxes exist for now or sort them.
             // Windows AIManager does not seem to do sorting in ProcessResults, but it does usage NMS?
             // I'll leave it raw for maximum control in Program.cs unless perf is bad.
             
             return predictions;
        }

        public void Dispose()
        {
            _onnxModel?.Dispose();
            _modelOptions?.Dispose();
        }
    }
}
