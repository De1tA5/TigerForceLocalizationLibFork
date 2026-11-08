using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;

namespace TigerForceLocalizationLib
{
    public sealed class StaticFieldLoader:ModSystem
    {
        //缓存所有
        private List<StaticFieldHandle> _staticFieldHandles = new();
        //生成令牌
        private object? _generation;

        public override void PostSetupContent()
        {
            // 每次初始化生成独立令牌。卸载后，队列中的旧任务不能重新创建绑定
            var generation = new object();
            _generation = generation;

            Main.QueueMainThreadAction(() =>
            {
                if (!ReferenceEquals(_generation, generation)) return;
                if (!ModLoader.TryGetMod("HardTarget", out Mod target)) return;
                try
                {
                    // 多程序集目标需沿用已有扫描器的 AssemblyManager 路径。
                    Type type = target.Code.GetType("HardTarget.StaticLabels") ?? throw new TypeLoadException("请将示例类型名改成实际静态字段的声明类型。");

                }
                catch (Exception ex) { Mod.Logger.Warn("静态字段示例未绑定：" + ex.Message); }
            });
        }


        //切换语言或重新加载时，再刷新所有句柄
        public override void OnLocalizationsLoaded()
        {

        }
    }
}
