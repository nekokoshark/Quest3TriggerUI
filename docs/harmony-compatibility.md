# Quest3TriggerUI v4.6.302 Harmony compatibility revision 1

修复在不同 Harmony 2.x / HarmonyX 环境下因 `Harmony.Patch` 返回类型变化导致的启动中断。兼容入口在安装补丁时缓存并调用运行时实际方法，不替换用户的 Harmony DLL，不在每帧反射。

## 已验证
- Harmony 2.0.0.0：补丁返回42、撤销后恢复7，通过。
- Harmony 2.3.0.0（目标安装）：同样通过。
- 编译后的载荷直接 `Harmony.Patch` 引用为0。
- 未做VR场景/完整启动验收；不宣称所有未来或历史版本、所有VaM补丁组合均兼容。Harmony 1.x 不在支持范围。

## 安装
正常退出目标 VaM，备份原插件目录。把ZIP里的 `BepInEx` 合并到VaM根目录，替换同名加载器和载荷。保留 `Quest3TriggerUI.payload.dll.disabled` 的 `.disabled` 后缀；不要另放未带后缀的payload DLL。

只含自研加载器与载荷，不含Harmony、BepInEx、游戏程序集、字典、原生库或第三方VAR。原安装的其他依赖及用户配置仍需保留。不要为本版本替换Assembly-CSharp.dll。NativeLibrary性能补丁依赖、其他Harmony API差异尚不能由本次Patch修复保证解决。

回退：退出VaM，恢复此前备份的加载器和载荷。此修订未修改游戏缓存、配置或游戏程序集。

## 从源码构建
Windows、Python 3、.NET Framework编译器及对应VaM/BepInEx引用文件：

```powershell
python tools/build_compatible.py --builder tools/build_payload.py --vam-root "F:\YOUR_VAM" --source src --out build --tag vHarmonyPortable --loader
```

必须使用上述兼容构建入口；旧构建入口不会自动改写源码中的直接Patch调用。新出现的其他调用接收者须加入改写列表，并检查输出载荷中直接Patch引用为0。
