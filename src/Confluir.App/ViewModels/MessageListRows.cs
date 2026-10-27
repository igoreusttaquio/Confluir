using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Confluir.App.Resources;
using Confluir.Core.Formatting;
using Confluir.Core.Models;

namespace Confluir.App.ViewModels;

/// <summary>Linha da lista de mensagens: cabeçalho de grupo por data ou mensagem.</summary>
public abstract class MessageListRow : ViewModelBase
{
    public virtual bool IsSelectable => true;
}

public sealed class DateHeaderRow(DateGroup group) : MessageListRow
{
    public override bool IsSelectable => false;

    public string Title { get; } = group switch
    {
        DateGroup.Today => Strings.Group_Today,
        DateGroup.Yesterday => Strings.Group_Yesterday,
        DateGroup.ThisWeek => Strings.Group_ThisWeek,
        _ => Strings.Group_Older,
    };
}

public sealed partial class MessageRow(MessageSummary summary, MailAccount account, string dateText, bool isRead)
    : MessageListRow
{
    public MessageSummary Summary { get; } = summary;
    public MailAccount Account { get; } = account;

    public string From => string.IsNullOrWhiteSpace(Summary.FromName) ? Summary.FromAddress : Summary.FromName;
    public string Subject => string.IsNullOrWhiteSpace(Summary.Subject) ? Strings.NoSubject : Summary.Subject;
    public string Preview => Summary.Preview;
    public string DateText { get; } = dateText;
    [ObservableProperty]
    public partial bool IsFlagged { get; set; } = summary.IsFlagged;
    public bool HasAttachments => Summary.HasAttachments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnread))]
    public partial bool IsRead { get; set; } = isRead;

    public bool IsUnread => !IsRead;
}

public static class MessageListBuilder
{
    /// <summary>Intercala cabeçalhos de data entre mensagens já ordenadas da mais recente para a mais antiga.</summary>
    public static List<MessageListRow> Build(
        IReadOnlyList<MessageSummary> messages,
        IReadOnlyDictionary<Guid, MailAccount> accounts,
        ISet<Guid> locallyRead,
        DateTimeOffset now,
        CultureInfo culture)
    {
        var rows = new List<MessageListRow>(messages.Count + 4);
        DateGroup? current = null;
        foreach (var m in messages)
        {
            var group = DateGrouping.GetGroup(m.Date, now, culture);
            if (group != current)
            {
                rows.Add(new DateHeaderRow(group));
                current = group;
            }
            var dateText = RelativeDateFormatter.Format(m.Date, now, culture, Strings.Group_Yesterday);
            rows.Add(new MessageRow(m, accounts[m.AccountId], dateText, m.IsRead || locallyRead.Contains(m.Id)));
        }
        return rows;
    }
}
