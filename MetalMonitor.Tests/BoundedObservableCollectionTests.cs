using MetalMonitor.Core;
using Xunit;

namespace MetalMonitor.Tests
{
    /// <summary>
    /// 有界可观察集合单测（STEP-4，D-6）：锁定"超容量丢最旧"与"先裁后插"两条语义。
    /// </summary>
    public class BoundedObservableCollectionTests
    {
        /// <summary>容量内追加：不触发裁剪，顺序与数量保持不变。</summary>
        [Fact]
        public void Add_WithinCapacity_KeepsAllItems()
        {
            var collection = new BoundedObservableCollection<int>(3);

            collection.Add(1);
            collection.Add(2);
            collection.Add(3);

            Assert.Equal(new[] { 1, 2, 3 }, collection);
        }

        /// <summary>超容量追加：只保留最新的 capacity 条，最旧的被挤出。</summary>
        [Fact]
        public void Add_BeyondCapacity_KeepsNewestItems()
        {
            var collection = new BoundedObservableCollection<int>(3);

            for (int i = 1; i <= 5; i++)
                collection.Add(i);

            Assert.Equal(new[] { 3, 4, 5 }, collection);
        }

        /// <summary>在满容量时从索引 0 插入：新元素保留，最旧元素被移除（验证"先裁后插"）。</summary>
        [Fact]
        public void InsertAtZero_AtCapacity_KeepsNewItemAndDropsOldest()
        {
            var collection = new BoundedObservableCollection<int>(3);
            collection.Add(1);
            collection.Add(2);
            collection.Add(3);

            collection.Insert(0, 0);

            Assert.Equal(new[] { 0, 2, 3 }, collection);
            Assert.Equal(3, collection.Count);
        }
    }
}
