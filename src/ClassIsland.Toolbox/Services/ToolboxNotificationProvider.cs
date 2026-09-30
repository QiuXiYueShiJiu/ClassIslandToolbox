using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;

namespace ClassIsland.Toolbox.Services;

/// <summary>
/// 幸运抽签的提醒提供方。
/// </summary>
/// <remarks>
/// <c>INotificationHostService.ShowNotification</c> 是 internal 的，插件够不着；
/// 走 <see cref="NotificationProviderBase"/> 注册成一个提醒提供方，
/// 就能拿到公开的 <c>ShowNotification</c>，点到谁就把名字推到主界面那条上。
/// 注册之后它也会出现在【应用设置】→【提醒】里，可以单独调音效、特效这些。
/// <para/>
/// <b>GUID 必须全局唯一。</b>宿主按 GUID 索引提醒提供方，
/// 两个插件用同一个 GUID 的话，同时装就会互相顶掉，
/// 表现是其中一个的提醒设置莫名其妙跟着另一个变。
/// </remarks>
[NotificationProviderInfo("3F6C1A87-5E24-4D9B-A1C0-8B7E2F4D6C53", "幸运抽签",
    "点到人之后在主界面上显示名字。")]
public class ToolboxNotificationProvider : NotificationProviderBase
{
}
