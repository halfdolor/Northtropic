using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;

namespace Northtropic.Services
{
    public class LocalPaddleOcrResult
    {
        public bool Success { get; set; }
        public string Text { get; set; } = string.Empty;
        public List<string> Lines { get; set; } = new List<string>();
        public List<PaddleOcrRegionInfo> Regions { get; set; } = new List<PaddleOcrRegionInfo>();
        public int ElapsedMs { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public class PaddleOcrRegionInfo
    {
        public string Text { get; set; } = string.Empty;
        public float Score { get; set; }
    }

    public static class LocalPaddleOcrEngine
    {
        private static FullOcrModel? _cachedModel;
        private static readonly SemaphoreSlim _modelLock = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _ocrExecutionLock = new SemaphoreSlim(1, 1);
        public static bool IsInitialized => _cachedModel != null;

        public static async Task<FullOcrModel?> GetOrLoadModelAsync()
        {
            if (_cachedModel != null) return _cachedModel;

            await _modelLock.WaitAsync();
            try
            {
                if (_cachedModel != null) return _cachedModel;

                // 下载并载入官方 ChineseV4 高精度中文/试卷识别模型
                _cachedModel = await OnlineFullModels.ChineseV4.DownloadAsync();
                return _cachedModel;
            }
            catch (Exception)
            {
                try
                {
                    // 降级尝试 V3 轻量模型
                    _cachedModel = await OnlineFullModels.ChineseV3.DownloadAsync();
                    return _cachedModel;
                }
                catch
                {
                    return null;
                }
            }
            finally
            {
                _modelLock.Release();
            }
        }

        public static async Task<LocalPaddleOcrResult> RecognizeAsync(byte[] imageBytes)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new LocalPaddleOcrResult();

            if (imageBytes == null || imageBytes.Length == 0)
            {
                result.Success = false;
                result.ErrorMessage = "输入图像字节数组为空！";
                return result;
            }

            try
            {
                var model = await GetOrLoadModelAsync();
                if (model == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "PaddleOCR 离线模型未就绪或初始化失败。";
                    return result;
                }

                PaddleOcrResult ocrResult;
                using (var raw = Cv2.ImDecode(imageBytes, ImreadModes.Color))
                {
                    if (raw.Empty())
                    {
                        result.Success = false;
                        result.ErrorMessage = "图像解码失败，请确认是否为标准图片格式。";
                        return result;
                    }

                    // 架构与性能优化：当输入图片单边超过 1600px 时进行等比平滑缩放，保护非托管内存并提升 3~5 倍识别效率
                    Mat src = raw;
                    bool needDisposeSrc = false;
                    int maxDimension = Math.Max(raw.Width, raw.Height);
                    if (maxDimension > 1600)
                    {
                        double scale = 1600.0 / maxDimension;
                        src = new Mat();
                        Cv2.Resize(raw, src, new OpenCvSharp.Size((int)(raw.Width * scale), (int)(raw.Height * scale)), interpolation: InterpolationFlags.Area);
                        needDisposeSrc = true;
                    }

                    try
                    {
                        // 原生非托管推理互斥锁：防止多用户高并发同时调用 C++ PaddlePredictor 引发访问冲突与内存损坏
                        await _ocrExecutionLock.WaitAsync();
                        try
                        {
                            using var ocr = new PaddleOcrAll(model)
                            {
                                Enable180Classification = false,
                                AllowRotateDetection = false
                            };
                            ocrResult = ocr.Run(src);
                        }
                        finally
                        {
                            _ocrExecutionLock.Release();
                        }
                    }
                    finally
                    {
                        if (needDisposeSrc)
                        {
                            src.Dispose();
                        }
                    }
                }
                sw.Stop();

                result.Success = true;
                result.Text = ocrResult.Text;
                result.ElapsedMs = (int)sw.ElapsedMilliseconds;

                if (ocrResult.Regions != null)
                {
                    foreach (var region in ocrResult.Regions)
                    {
                        if (!string.IsNullOrWhiteSpace(region.Text))
                        {
                            result.Lines.Add(region.Text.Trim());
                            result.Regions.Add(new PaddleOcrRegionInfo
                            {
                                Text = region.Text.Trim(),
                                Score = region.Score
                            });
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Success = false;
                result.ElapsedMs = (int)sw.ElapsedMilliseconds;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }
    }
}
