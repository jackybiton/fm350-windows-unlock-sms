# Dell DW5931E / Fibocom FM350-GL Windows FCC 解锁

在非 Dell 主板上为 Dell DW5931E（Fibocom FM350-GL）解除启动后的 FCC 软件锁，并配置 Windows 开机自动解锁。

本项目不会修改或刷写模组固件，也不包含 Dell 驱动、认证程序或其他专有二进制文件。

## 已验证环境

- 模组：Dell DW5931E / Fibocom FM350-GL
- PCI ID：`14C3:4D75`，Dell 子系统 `1028:5931`
- 固件：`81600.0000.00.29.23.28_CT / F32`
- 系统：Windows 10 IoT Enterprise LTSC 19044 x64
- 主板：非 Dell X99 主板
- 网络：中国电信，5G SA

其他固件、运营商和 OEM 版本尚未验证。

## 问题表现

驱动、SIM、IMEI 和 ICCID 均可正常识别，但 Windows 显示：

```text
Hardware radio state : Off
Software radio state : On
```

Dell 的 `WWAN ModemAuthenticator` 服务会检查 Dell SMBIOS。在普通台式机主板上，日志可能出现：

```text
CheckDellPcIdentifyRegistry return false
Can not find type: 1 in SMBIOS table
DoFccUnlock: Not find SMBIOS FCC Type
```

## 快速安装

先安装适合该模组的 Dell 驱动。然后以管理员身份打开 Windows PowerShell：

```powershell
git clone https://github.com/OWNER/fm350-dell-fcc-unlock-windows.git
cd fm350-dell-fcc-unlock-windows
Set-ExecutionPolicy -Scope Process Bypass
.\Install.ps1
```

安装程序会：

1. 备份匹配网卡实例的 `WWANFeatureAT` 注册表值，以及 Dell 认证服务的启动状态。
2. 启用驱动的 COM AT 端口。
3. 停止并禁用在非 Dell SMBIOS 上无法工作的原认证服务。
4. 安装 `SYSTEM` 身份运行的开机任务 `FM350 FCC Unlock`。
5. 立即尝试一次解锁；若 AT 端口刚启用，重启一次后任务会自动重试。

日志位置：

```text
C:\ProgramData\FM350Unlock\unlock.log
```

## 手动运行与验证

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Unlock-FM350.ps1
netsh mbn show radio interface=*
netsh mbn show connection interface=*
```

成功时模组返回 `+GTFCCLOCKVER: 1`，Windows 的硬件无线状态变为 `On`。

无需连接模组即可运行算法自测：

```powershell
.\Unlock-FM350.ps1 -SelfTest
```

## 解锁算法

Dell 认证程序在 Dell SMBIOS OEM 结构不可用时无法取得 FCC 种子。DW5931E 使用的种子为 ASCII 字符串：

```text
DW5931EFCCLOCK
```

算法如下：

1. 发送 `AT+GTFCCLOCKGEN`，取得 32 位 challenge。
2. 计算 `SHA-256("DW5931EFCCLOCK")`，取 digest 前 4 字节，得到 `49 09 b5 a4`。
3. 将 challenge 的 4 个大端字节与上述 4 字节连接，再计算 SHA-256。
4. 取新 digest 前 4 字节作为大端无符号整数。
5. 以十进制发送 `AT+GTFCCLOCKVER=<response>`。

这与 Lenovo FM350-GL 使用的厂商哈希不同，不能直接套用 Lenovo 的 `3df8c719`。

完整的诊断、逆向定位与验证过程见 [docs/RESEARCH.md](docs/RESEARCH.md)。文档中的设备身份、SIM 信息、主机名和公网/地址标识均已删除。

## 卸载

以管理员身份运行：

```powershell
.\Uninstall.ps1
```

卸载程序会删除开机任务，恢复安装前记录的注册表值和 Dell 服务启动类型。日志与回滚记录会保留在 `C:\ProgramData\FM350Unlock`。

## 故障排查

- 找不到 COM AT 端口：安装后重启一次，并确认设备管理器中出现名称包含 `FM350` 或 `DW5931E` 的 `COM AT` 设备。
- 仍显示硬件无线关闭：查看 `unlock.log`，确认没有其他程序占用 AT 端口。
- SIM 已识别但未注册：检查天线、APN、运营商频段和套餐；这已经不是 FCC 锁问题。
- USB 模式：本项目针对 PCIe/MBIM 驱动路径，不负责切换或刷写 USB composition。

## 安全与合规

仅在你拥有或获授权维护的设备上使用。使用蜂窝射频设备时，应遵守所在地的无线电法规、运营商规则及硬件认证要求。项目按 MIT 许可证提供，不附带任何担保。

## 致谢

- ModemManager 社区公开的 FM350-GL FCC challenge/response 工作为协议分析提供了参考。
- Fibocom、Dell 和 Intel/MediaTek 保留各自产品及商标权利；本项目与这些厂商无隶属关系。
