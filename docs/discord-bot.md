# Discord 机器人

LauncherGo 的 Discord 集成与 OneBot/QQ 机器人独立运行。配置文件位于工作区的 `.runtime/discordbot/discord-settings.json`，不会读取或修改 QQ 数据库。

在 Discord Developer Portal 创建应用并启用 Bot：

- 使用 `bot` 和 `applications.commands` scope 邀请机器人。
- 授予读取频道、发送消息、读取历史消息和上传附件权限。
- 将 Bot Token 填入 LauncherGo 的“连接 -> Discord”页面，不要提交到仓库或日志。

Discord 使用 Guild Slash Commands。每个 Profile 可以绑定多个 Guild/Channel；未绑定频道不会执行 Profile 命令。管理员可以通过 Discord 用户 Snowflake ID 或角色 Snowflake ID 配置。机器人不需要 Message Content Intent。

首次启动会在配置中出现的 Guild 注册 `/help`、`/send`、`/server`、`/bind`、`/myinfo`、`/modslist`、`/modfile`、`/modfileall` 和 `/custom`。名称符合 Discord 规则的自定义指令会额外注册为原生 Slash Command，其他指令可通过 `/custom` 调用。命令说明和状态结果使用绑定 Profile 的 `ServerLanguage`；支持服务端设置页列出的全部语言及区域代码回退。同一个 Guild 的 Slash Command 是 Guild 级资源，若它绑定多个不同语言的 Profile，命令说明使用绑定列表中第一个有效 Profile 的语言，命令结果仍使用实际目标 Profile 的语言。

`/server` 使用原生子命令 `status`、`players`、`start`、`stop` 和 `password`；服务器控制、服务端指令和文件导出要求配置的管理员用户或角色。Discord 玩家绑定保存在 `.runtime/discordbot/player-bindings.json`，与 QQ 绑定数据库隔离。配置修改后点击保存会自动重载正在运行的 Discord 机器人。服务器语言或 Discord 命令缓存变化后，可点击“重新部署命令”通过 Guild Bulk Overwrite 重置命令，无需清空 Token 或绑定。

绑定和自定义指令在 UI 中使用表格编辑。绑定行选择 Profile 后填写 Guild ID 和 Channel ID；自定义指令行选择文本或图片类型，图片通过文件选择按钮指定。保存时会校验 Token、Snowflake ID、Profile、绑定 ID、指令名称和内容，错误会显示在对应配置区域并阻止保存。
