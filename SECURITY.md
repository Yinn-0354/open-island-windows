# Security Policy

## 报告漏洞

如果你在 Open Island 里发现安全问题，请**不要**直接开 public issue。

走以下任一渠道私下告知：

- **GitHub Security Advisories**（推荐）：[New draft advisory](../../security/advisories/new) —— 仅维护者能看
- **Issue（仅限非敏感）**：如果问题不暴露任何利用细节（比如"hook 装错路径会导致 X 不工作"这种功能性 bug），可以直接开 [issue](../../issues)

我们会在 7 天内首次回复，30 天内给出修复或缓解方案。

## 关注点

Open Island 主要的攻击面：

- **Hook 协议** —— `OpenIsland.Hooks/Program.cs` 从 stdin 读 untrusted JSON。所有解析失败必须 fail-open（不影响 Claude 运行）+ 不能执行任意命令
- **Named Pipe 桥** —— `OpenIsland_Pipe` 默认仅本机访问，不绑定网络。任何放宽 ACL 的改动必须在 PR 里讨论
- **SendInput 注入** —— 灵动岛的 1/2/3 按钮通过 SendInput 把按键塞给目标终端窗口。仅在用户主动点击 + 焦点已切到终端时触发，避免无脑注入
- **路径处理** —— transcript 解析、JumpTarget 构造对路径只信任 OS API（`Path.GetFileName` 等），不做手撸字符串拼接

## 不在范围内

- 用户自己 Claude Code 配置不当（如设了 `dangerouslySkipPermissions`）的间接风险
- 第三方 NuGet 依赖的 CVE —— 请直接报到对应上游
