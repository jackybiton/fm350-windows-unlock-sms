# DW5931E / FM350-GL Windows FCC 解锁研究记录

本文记录一次在非 Dell X99 主板上排查 Dell DW5931E（Fibocom FM350-GL）的过程。所有设备身份、SIM 身份、主机名和实际网络地址均已移除。

## 1. 排除 SIM、驱动和基础硬件故障

Windows 能读取以下信息：

- 模组型号、固件版本和设备 ID
- SIM Ready 状态
- IMEI、ICCID 与运营商信息
- PCIe/MBIM 网卡设备，设备管理器状态正常

但无线状态存在决定性差异：

```powershell
netsh mbn show radio interface=*
```

```text
Hardware radio state : Off
Software radio state : On
```

软件开关已经打开，而硬件无线仍被关闭，因此问题不在 Windows 飞行模式或普通 APN 配置。

## 2. 定位 Dell FCC 认证失败

Dell 驱动包安装了 `WWAN ModemAuthenticator` 服务。服务日志反复出现：

```text
CheckDellPcIdentifyRegistry return false
Can not find type: 1 in SMBIOS table
DoFccUnlock: Not find SMBIOS FCC Type
```

Dell 安装程序也会因主板制造商不是 Dell 而拒绝运行。由此确认：模组处于 FCC 软件锁定状态，而 Dell 解锁服务依赖 Dell SMBIOS OEM 数据，无法在普通 X99 主板上完成认证。

## 3. 启用 AT 端口

Dell 网卡驱动实例包含 `WWANFeatureAT` 配置。将匹配 FM350/DW5931E 的网卡类实例设置为 DWORD `1`，重新枚举设备或重启后，会出现类似设备：

```text
Dell Global 5G(DW5931E) COM AT (COMx)
```

相关注册表路径位于：

```text
HKLM\SYSTEM\CurrentControlSet\Control\Class\
{4d36e972-e325-11ce-bfc1-08002be10318}\####
```

安装脚本按设备描述和硬件 ID 精确匹配，不依赖固定的 `####` 编号，并会记录旧值供卸载恢复。

该 UMDF AT 端口无法稳定地由 `.NET SerialPort` 或 Windows PowerShell 5.1 的 `System.IO.File.Open` 打开。可靠方法是调用 Win32 `CreateFile("\\\\.\\COMx")`，再用 `FileStream` 包装返回的原生句柄。

## 4. 验证模组状态

AT 端口可读取：

```text
ATI
AT+CFUN?
AT+CPIN?
AT+GTUSBMODE?
```

样机返回 FM350-GL、`CFUN: 1`、`CPIN: READY` 和 USB composition 41。这进一步排除了 SIM PIN、模组关机及驱动完全失效。

## 5. Lenovo 算法为何失败

ModemManager 已公开 Lenovo FM350-GL 的 challenge/response 流程，其 vendor hash 为：

```text
3df8c719
```

将它用于 Dell DW5931E 时，模组返回：

```text
+GTFCCLOCKVER: 0
```

这说明 FM350 OEM 版本共用协议，但 OEM vendor hash 不同。此后没有继续随机尝试，也没有刷写通用固件。

## 6. 从 Dell 认证程序还原算法

对 Dell 签名的 `WWANModemAuthenticator.exe` 做静态分析，定位到以下逻辑：

1. 从 SMBIOS OEM Type `0x85`、subtype `0x05` 复制 14 字节 FCC 字符串。
2. 调用内置 SHA-256 实现计算该字符串的摘要。
3. 取摘要前 4 字节，与模组 challenge 的 4 字节连接。
4. 再计算一次 SHA-256，并把摘要前 4 字节提交给模组。

程序中的 DW5931E 默认 FCC 字符串为：

```text
DW5931EFCCLOCK
```

其 SHA-256 为：

```text
4909b5a45d9044283599c5f521aa6ce0387ca4c58c13bb68f52d6ef849cc03c3
```

因此 Dell vendor hash 是前 4 字节：

```text
4909b5a4
```

等价伪代码：

```text
vendor = SHA256(ASCII("DW5931EFCCLOCK"))[0:4]
challenge = big_endian_bytes(AT+GTFCCLOCKGEN)
response = big_endian_uint32(SHA256(challenge || vendor)[0:4])
AT+GTFCCLOCKVER=<response as decimal>
```

## 7. 成功判据

正确应答后，模组返回：

```text
+GTFCCLOCKVER: 1
```

随后 Windows 状态变为：

```text
Hardware radio state : On
Software radio state : On
Interface State      : Connected
Register State       : Home
Provider Data Class  : 5G_SA
```

样机最终在中国电信 `ctnet` APN 下完成 5G SA 注册，并通过绑定蜂窝接口的 HTTP 请求验证了真实数据通路。

## 8. 持久化策略

FCC 锁可能在冷启动或模组复位后重新出现。项目采用以下可逆方案：

- 安装自包含的 `.NET` Windows 服务，以 `LocalSystem` 身份自动启动。
- 服务缓存有效的 WinRT modem 实例；实例失效时用 `GetDeviceSelector`、`DeviceInformation.FindAllAsync` 和 `FromId` 重新枚举，不依赖服务账户的“默认”蜂窝接口。
- 无线关闭时立即执行已通过冷启动实机验证的 MBIM 命令 1 流程：通用 `SUBSYS_35001CF8` 使用 Set → Set → post-Query，Dell `SUBSYS_59311028` 使用 Query → Set → Set → post-Query；不混用 AT 协议。
- 无线已打开时只观察状态；运行中设备复位重新触发 FCC 锁时自动补解锁。
- 同一 PnP 设备生命周期内成功后不再重复认证；只有设备真正消失或重新枚举后才清除该状态。这样既能在模组复位后补解锁，也不会把用户主动关闭蜂窝无线误判为需要持续认证。
- 关闭 Dell 网卡实例及其 `VID_8087&PID_0B62` COM AT 子设备的选择性挂起、断开后设备休眠、PCI D3cold、WDF directed power transition 及 working-state idle。Dell 网卡 INF 默认声明 `PciD3ColdSupported.HW`，网卡和 COM 驱动均写入 `WdfDirectedPowerTransitionEnable=1`；样机在系统级 PCIe ASPM 已关闭时仍出现与掉卡同步的 WHEA AER `Surprise Down` 和 Code 10/`STATUS_DEVICE_POWER_FAILURE`。这些设置只用于排除不兼容的低功耗转换，不能恢复已经卡死的设备，也不能修复插槽、转接板或供电链路的电气问题。
- 禁用依赖 Dell SMBIOS 且会持续失败的原认证服务；删除旧版本的 AT 开机任务。
- 安装时保存注册表值和服务状态，换槽或重新运行安装程序时补充发现新实例，卸载时逐项恢复。

没有修改 Dell 可执行文件、绕过 Windows 驱动签名，也没有刷写模组固件。

## 9. 为什么不修改网卡内核驱动

Dell 驱动包把数据路径和 FCC 认证拆成了两个组件：

- `WwanNet.sys` 是签名的 KMDF/NDIS 内核网卡驱动，负责 PCIe/MBIM 数据路径。
- `WWANModemAuthenticator.exe` 是独立的 `LocalSystem` 用户态服务，负责读取 SMBIOS 并完成 FCC 认证。

认证程序的静态字符串包含 `Dell Inc.`、`SystemManufacturer`、`CheckDellPcIdentifyRegistry return false`、`Not find SMBIOS FCC Type`、`DoFccUnlock` 和 `DW5931EFCCLOCK`。它还包含 `Query_INTEL_FCC_MBIM_Extension` 与 `Set_INTEL_FCC_MBIM_Extension`。进一步分析确认原厂服务通过私有 MBIM 命令 1 执行认证，主板厂商判断不属于 `WwanNet.sys`。

直接修改 `WwanNet.sys` 或 INF 会破坏微软硬件兼容性签名，并可能触发内核代码完整性限制。修改原厂认证程序同样会使其签名失效，且不适合分发。项目因此保持原签名驱动不动，以独立、可审计的 `.NET` 用户态 MBIM 服务替换已禁用的原服务。

## 10. 最终验证结论与问题归属

冷启动实测确认，独立服务能在设备出现后通过私有 MBIM 命令 1 完成 FCC challenge/response，使 Windows 硬件无线状态变为 `On`，并自动连接中国电信 5G SA。由 post-Query 第二字段变为 `1`、Windows 注册状态和实际数据请求共同确认，算法、载荷、MBIM 扩展路径以及开机服务集成均有效；正式解锁路径不依赖 COM AT 或物理 USB 线。

样机的长期压力测试没有形成同等的硬件稳定性结论：

- FM350 发生过 PCIe Surprise Down、Code 10 和 `STATUS_DEVICE_POWER_FAILURE`。
- 关闭蜂窝连接后，运行高负载应用仍会发生掉卡，说明故障不由数据业务流量触发。
- 最终移除 FM350 后，Samsung NVMe 仍在游戏负载下从 PCIe 总线消失，并产生 STORPORT/stornvme WHEA 记录及大量 Disk/NTFS 错误。

这些证据将“FCC 认证成功”和“主机长期稳定”明确分开：本项目解决了非 Dell SMBIOS 上的 FCC 软件认证，但该 X99 样机还存在独立的主板、PCIe 插槽、转接板、供电或信号完整性问题。设备电源策略只能绕开可能不兼容的低功耗状态，不能保证或修复电气稳定性。因此，本项目不把该样机后续的掉卡、蓝屏或掉盘归因于 MBIM 应答值，也不宣称已通过长期稳定性验证。

0.4.1 对应的代码和回滚变化集中记录于仓库根目录的 [CHANGELOG.md](../CHANGELOG.md)。
