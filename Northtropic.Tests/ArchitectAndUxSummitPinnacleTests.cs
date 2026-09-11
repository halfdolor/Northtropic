using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxSummitPinnacleTests
    {
        [Fact]
        public void CheckFillInBlankMatch_ScientificUnits_AreaAndVolumeEquivalence()
        {
            // 1. 物理/化学/几何常见面积与体积单位容错规范
            Assert.True(PracticeService.CheckFillInBlankMatch("25cm²", "25"));
            Assert.True(PracticeService.CheckFillInBlankMatch("25平方厘米", "25"));
            Assert.True(PracticeService.CheckFillInBlankMatch("25cm²", "25平方厘米"));
            Assert.True(PracticeService.CheckFillInBlankMatch("100m²", "100平方米"));
            Assert.True(PracticeService.CheckFillInBlankMatch("100平方分米", "100dm²"));

            // 体积与容积单位
            Assert.True(PracticeService.CheckFillInBlankMatch("50cm³", "50立方厘米"));
            Assert.True(PracticeService.CheckFillInBlankMatch("50m³", "50立方米"));
            Assert.True(PracticeService.CheckFillInBlankMatch("500毫升", "500ml"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2升", "2l"));
        }

        [Fact]
        public void CheckFillInBlankMatch_ScientificUnits_ConcentrationTorqueAndSpeed()
        {
            // 2. 物质的量浓度、力矩与速度复合物理单位
            Assert.True(PracticeService.CheckFillInBlankMatch("2mol/L", "2摩尔/升"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2mol·L^-1", "2mol/L"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1.5g/mL", "1.5克/毫升"));

            // 力矩与速度
            Assert.True(PracticeService.CheckFillInBlankMatch("50N·m", "50牛·米"));
            Assert.True(PracticeService.CheckFillInBlankMatch("50牛顿·米", "50N·m"));
            Assert.True(PracticeService.CheckFillInBlankMatch("60公里/小时", "60km/h"));
            Assert.True(PracticeService.CheckFillInBlankMatch("60公里每小时", "60公里/小时"));
        }

        [Fact]
        public void CheckFillInBlankMatch_ScientificUnits_ElectromagneticsAndPressure()
        {
            // 3. 电学阻值、电功率、功与压强复合工程单位
            Assert.True(PracticeService.CheckFillInBlankMatch("5kΩ", "5千欧"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10MΩ", "10兆欧"));
            Assert.True(PracticeService.CheckFillInBlankMatch("100kPa", "100千帕"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2kW", "2千瓦"));
            Assert.True(PracticeService.CheckFillInBlankMatch("5kJ", "5千焦"));
        }

        [Fact]
        public void CheckFillInBlankMatch_ChineseFractionsAndPercentages_ParsedAccurately()
        {
            // 4. 中文分数与百分比深度解构
            Assert.True(PracticeService.CheckFillInBlankMatch("二分之一", "1/2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("二分之一", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.5", "二分之一"));
            Assert.True(PracticeService.CheckFillInBlankMatch("三分之二", "2/3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("四分之三", "3/4"));
            Assert.True(PracticeService.CheckFillInBlankMatch("四分之三", "0.75"));

            // 中文百分比
            Assert.True(PracticeService.CheckFillInBlankMatch("百分之二十五", "25%"));
            Assert.True(PracticeService.CheckFillInBlankMatch("百分之二十五", "0.25"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.25", "百分之二十五"));
            Assert.True(PracticeService.CheckFillInBlankMatch("百分之五十", "50%"));
            Assert.True(PracticeService.CheckFillInBlankMatch("百分之五十", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("百分之75", "75%"));
            Assert.True(PracticeService.CheckFillInBlankMatch("百分之百", "100%"));
        }

        [Fact]
        public void CheckFillInBlankMatch_EmptySetAndNoSolution_UnifiedEquivalence()
        {
            // 5. 空集、无解、无实数解变体统一归一化为 ∅
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\varnothing", "∅"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\emptyset", @"\varnothing"));
            Assert.True(PracticeService.CheckFillInBlankMatch("Ø", "∅"));
            Assert.True(PracticeService.CheckFillInBlankMatch("ø", "∅"));
            Assert.True(PracticeService.CheckFillInBlankMatch("空集", @"\varnothing"));
            Assert.True(PracticeService.CheckFillInBlankMatch("无实数解", "无解"));
            Assert.True(PracticeService.CheckFillInBlankMatch("无实根", "无解"));
            Assert.True(PracticeService.CheckFillInBlankMatch("无实数根", "∅"));
        }

        [Fact]
        public void CheckFillInBlankMatch_AnglesAndDegrees_UnifiedEquivalence()
        {
            // 6. 角度与度数表示
            Assert.True(PracticeService.CheckFillInBlankMatch("60°", "60度"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"60^\circ", "60°"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"60^{\circ}", "60度"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"45\circ", "45°"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"90\text{°}", "90°"));
        }

        [Fact]
        public async Task SystemHealthService_StorageTelemetry_QueriesMetricsAccurately()
        {
            // 7. 系统架构健康服务 SQLite 存储指标与磁盘空间测量
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var healthService = new SystemHealthService(context);
                var dto = await healthService.GetSystemHealthAsync();

                Assert.NotNull(dto);
                Assert.True(dto.PageSize >= 0);
                Assert.True(dto.PageCount >= 0);
                Assert.True(dto.FreelistCount >= 0);
                Assert.True(dto.FragmentationRatio >= 0.0 && dto.FragmentationRatio <= 1.0);
                Assert.False(string.IsNullOrWhiteSpace(dto.FragmentationFormatted));
                Assert.False(string.IsNullOrWhiteSpace(dto.DiskFreeSpaceFormatted));
                Assert.True(dto.HealthScore >= 0 && dto.HealthScore <= 100);
            }
        }

        [Fact]
        public async Task PracticeService_ConcurrentNormalizationStressTest_MaintainsIntegrity()
        {
            // 8. 高并发密集判题压力测试
            var tasks = new List<Task>();
            for (int i = 0; i < 50; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    Assert.True(PracticeService.CheckFillInBlankMatch("百分之二十五", "0.25"));
                    Assert.True(PracticeService.CheckFillInBlankMatch("二分之一", "0.5"));
                    Assert.True(PracticeService.CheckFillInBlankMatch("25cm²", "25平方厘米"));
                    Assert.True(PracticeService.CheckFillInBlankMatch("50N·m", "50牛·米"));
                    Assert.True(PracticeService.CheckFillInBlankMatch(@"\varnothing", "空集"));
                    Assert.True(PracticeService.CheckFillInBlankMatch("60°", @"60^{\circ}"));
                }));
            }
            await Task.WhenAll(tasks);
        }
    }
}
