Imports System.Text.RegularExpressions
Imports System.Windows.Documents
Imports System.Windows.Navigation

''' <summary>
''' 把常用 Markdown 转换为 PTL 主题下的 WPF FlowDocument。
''' 支持标题、段落、粗体、斜体、链接、列表、引用、行内代码、代码块和分隔线。
''' </summary>
Public Module ModPtlMarkdown

    Private Class MarkdownListLine
        Public Property Indent As Integer
        Public Property IsOrdered As Boolean
        Public Property StartNumber As Integer = 1
        Public Property Text As String = ""
    End Class

    Private ReadOnly HeadingPattern As New Regex(
        "^\s{0,3}(?<marks>#{1,6})\s+(?<text>.*?)\s*#*\s*$",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)
    Private ReadOnly QuotePattern As New Regex(
        "^\s{0,3}>\s?(?<text>.*)$",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)
    Private ReadOnly ListPattern As New Regex(
        "^(?<indent>\s*)(?<marker>[-+*]|\d+[.)])\s+(?<text>.*)$",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)
    Private ReadOnly HorizontalRulePattern As New Regex(
        "^\s{0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)
    Private ReadOnly InlinePattern As New Regex(
        "(?<bold>\*\*.+?\*\*)|(?<bold2>__.+?__)|(?<code>`[^`]+`)|" &
        "(?<link>\[[^\]]+\]\([^)]+\))|(?<italic>\*[^*]+\*)|(?<italic2>_[^_]+_)",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)
    Private ReadOnly LinkPattern As New Regex(
        "^\[(?<text>[^\]]+)\]\((?<url>[^)]+)\)$",
        RegexOptions.Compiled Or RegexOptions.CultureInvariant)

    Public Function PtlCreateMarkdownDocument(Text As String) As FlowDocument
        Dim Document As New FlowDocument With {
            .PagePadding = New Thickness(0),
            .ColumnWidth = Double.PositiveInfinity,
            .FontFamily = New Media.FontFamily("Microsoft YaHei UI"),
            .FontSize = 14,
            .LineHeight = 22,
            .Foreground = PtlGetBrush("ColorBrush1", Media.Brushes.Black)
        }
        Dim Lines As String() = If(Text, "").
            Replace(vbCrLf, vbLf).
            Replace(vbCr, vbLf).
            Split(vbLf)
        PtlRenderMarkdownLines(Lines, Document.Blocks)
        If Document.Blocks.Count = 0 Then
            Document.Blocks.Add(New Paragraph(New Run("此版本未提供更新日志。")))
        End If
        Return Document
    End Function

    Private Sub PtlRenderMarkdownLines(
        Lines As String(), Target As BlockCollection)
        Dim Index As Integer = 0
        While Index < Lines.Length
            Dim CurrentLine As String = Lines(Index)
            If String.IsNullOrWhiteSpace(CurrentLine) Then
                Index += 1
                Continue While
            End If

            Dim Trimmed As String = CurrentLine.TrimStart()
            If Trimmed.StartsWith("```", StringComparison.Ordinal) OrElse
               Trimmed.StartsWith("~~~", StringComparison.Ordinal) Then
                Dim Fence As String = Trimmed.Substring(0, 3)
                Dim CodeLines As New List(Of String)
                Index += 1
                While Index < Lines.Length AndAlso
                      Not Lines(Index).TrimStart().StartsWith(
                          Fence, StringComparison.Ordinal)
                    CodeLines.Add(Lines(Index))
                    Index += 1
                End While
                If Index < Lines.Length Then Index += 1
                Dim CodeRun As New Run(String.Join(vbCrLf, CodeLines)) With {
                    .FontFamily = New Media.FontFamily("Consolas"),
                    .FontSize = 12.5
                }
                Target.Add(New Paragraph(CodeRun) With {
                    .Margin = New Thickness(0, 2, 0, 10),
                    .Padding = New Thickness(10, 8, 10, 8),
                    .Background = PtlGetBrush(
                        "ColorBrushSemiTransparent", Media.Brushes.WhiteSmoke),
                    .BorderThickness = New Thickness(1),
                    .BorderBrush = PtlGetBrush(
                        "ColorBrushGray1", Media.Brushes.LightGray)
                })
                Continue While
            End If

            Dim HeadingMatch As Match = HeadingPattern.Match(CurrentLine)
            If HeadingMatch.Success Then
                Dim Level As Integer = HeadingMatch.Groups("marks").Value.Length
                Dim HeadingParagraph As New Paragraph With {
                    .FontSize = Math.Max(17, 25 - Level * 2),
                    .FontWeight = FontWeights.SemiBold,
                    .Foreground = PtlGetBrush("ColorBrush2", Media.Brushes.Black),
                    .Margin = New Thickness(0, If(Target.Count = 0, 0, 14), 0, 8)
                }
                PtlRenderInlineMarkdown(
                    HeadingMatch.Groups("text").Value,
                    HeadingParagraph.Inlines,
                    False,
                    False)
                Target.Add(HeadingParagraph)
                Index += 1
                Continue While
            End If

            If HorizontalRulePattern.IsMatch(CurrentLine) Then
                Target.Add(New Paragraph With {
                    .Margin = New Thickness(0, 8, 0, 13),
                    .BorderThickness = New Thickness(0, 0, 0, 1),
                    .BorderBrush = PtlGetBrush(
                        "ColorBrushGray1", Media.Brushes.LightGray)
                })
                Index += 1
                Continue While
            End If

            If QuotePattern.IsMatch(CurrentLine) Then
                Dim QuoteLines As New List(Of String)
                While Index < Lines.Length
                    Dim QuoteMatch As Match = QuotePattern.Match(Lines(Index))
                    If Not QuoteMatch.Success Then Exit While
                    QuoteLines.Add(QuoteMatch.Groups("text").Value)
                    Index += 1
                End While
                Dim QuoteSection As New Section With {
                    .Margin = New Thickness(0, 2, 0, 10),
                    .Padding = New Thickness(12, 7, 10, 2),
                    .BorderThickness = New Thickness(3, 0, 0, 0),
                    .BorderBrush = PtlGetBrush("ColorBrush3", Media.Brushes.SteelBlue),
                    .Background = PtlGetBrush(
                        "ColorBrushSemiTransparent", Media.Brushes.WhiteSmoke)
                }
                PtlRenderMarkdownLines(QuoteLines.ToArray(), QuoteSection.Blocks)
                Target.Add(QuoteSection)
                Continue While
            End If

            Dim FirstListLine As MarkdownListLine =
                PtlTryParseMarkdownListLine(CurrentLine)
            If FirstListLine IsNot Nothing Then
                Dim MarkdownList As New System.Windows.Documents.List With {
                    .Margin = New Thickness(18, 2, 0, 10),
                    .MarkerStyle = If(FirstListLine.IsOrdered,
                        TextMarkerStyle.Decimal, TextMarkerStyle.Disc)
                }
                If FirstListLine.IsOrdered Then
                    MarkdownList.StartIndex = FirstListLine.StartNumber
                End If
                While Index < Lines.Length
                    Dim ListLine As MarkdownListLine =
                        PtlTryParseMarkdownListLine(Lines(Index))
                    If ListLine Is Nothing OrElse
                       ListLine.Indent <> FirstListLine.Indent OrElse
                       ListLine.IsOrdered <> FirstListLine.IsOrdered Then
                        Exit While
                    End If
                    Dim ItemParagraph As New Paragraph With {
                        .Margin = New Thickness(0, 0, 0, 3)
                    }
                    PtlRenderInlineMarkdown(
                        ListLine.Text, ItemParagraph.Inlines, False, False)
                    Dim ListItemValue As New ListItem With {
                        .Margin = New Thickness(0, 0, 0, 2)
                    }
                    ListItemValue.Blocks.Add(ItemParagraph)
                    MarkdownList.ListItems.Add(ListItemValue)
                    Index += 1
                End While
                Target.Add(MarkdownList)
                Continue While
            End If

            Dim ParagraphLines As New List(Of String)
            While Index < Lines.Length AndAlso
                  Not String.IsNullOrWhiteSpace(Lines(Index)) AndAlso
                  Not PtlIsMarkdownBlockStart(Lines(Index))
                ParagraphLines.Add(Lines(Index).Trim())
                Index += 1
            End While
            If ParagraphLines.Count = 0 Then
                ParagraphLines.Add(CurrentLine.Trim())
                Index += 1
            End If
            Dim ParagraphValue As New Paragraph With {
                .Margin = New Thickness(0, 0, 0, 9)
            }
            PtlRenderInlineMarkdown(
                String.Join(" ", ParagraphLines),
                ParagraphValue.Inlines,
                False,
                False)
            Target.Add(ParagraphValue)
        End While
    End Sub

    Private Function PtlTryParseMarkdownListLine(
        Line As String) As MarkdownListLine
        Dim MatchValue As Match = ListPattern.Match(Line)
        If Not MatchValue.Success Then Return Nothing
        Dim Marker As String = MatchValue.Groups("marker").Value
        Dim IsOrdered As Boolean = Char.IsDigit(Marker(0))
        Dim StartNumber As Integer = 1
        If IsOrdered Then
            Integer.TryParse(
                Marker.TrimEnd("."c, ")"c), StartNumber)
            If StartNumber <= 0 Then StartNumber = 1
        End If
        Return New MarkdownListLine With {
            .Indent = MatchValue.Groups("indent").Value.Length,
            .IsOrdered = IsOrdered,
            .StartNumber = StartNumber,
            .Text = MatchValue.Groups("text").Value
        }
    End Function

    Private Function PtlIsMarkdownBlockStart(Line As String) As Boolean
        Dim Trimmed As String = If(Line, "").TrimStart()
        Return Trimmed.StartsWith("```", StringComparison.Ordinal) OrElse
            Trimmed.StartsWith("~~~", StringComparison.Ordinal) OrElse
            HeadingPattern.IsMatch(Line) OrElse
            QuotePattern.IsMatch(Line) OrElse
            ListPattern.IsMatch(Line) OrElse
            HorizontalRulePattern.IsMatch(Line)
    End Function

    Private Sub PtlRenderInlineMarkdown(
        Text As String,
        Target As InlineCollection,
        IsBold As Boolean,
        IsItalic As Boolean)
        Dim LastIndex As Integer = 0
        For Each MatchValue As Match In InlinePattern.Matches(If(Text, ""))
            If MatchValue.Index > LastIndex Then
                PtlAddMarkdownText(
                    Target,
                    Text.Substring(LastIndex, MatchValue.Index - LastIndex),
                    IsBold,
                    IsItalic)
            End If
            Dim Token As String = MatchValue.Value
            If Token.StartsWith("**", StringComparison.Ordinal) Then
                PtlRenderInlineMarkdown(
                    Token.Substring(2, Token.Length - 4),
                    Target, True, IsItalic)
            ElseIf Token.StartsWith("__", StringComparison.Ordinal) Then
                PtlRenderInlineMarkdown(
                    Token.Substring(2, Token.Length - 4),
                    Target, True, IsItalic)
            ElseIf Token.StartsWith("`", StringComparison.Ordinal) Then
                Target.Add(New Run(Token.Substring(1, Token.Length - 2)) With {
                    .FontFamily = New Media.FontFamily("Consolas"),
                    .Background = PtlGetBrush(
                        "ColorBrushSemiTransparent", Media.Brushes.WhiteSmoke),
                    .FontWeight = If(IsBold, FontWeights.Bold, FontWeights.Normal),
                    .FontStyle = If(IsItalic, FontStyles.Italic, FontStyles.Normal)
                })
            ElseIf Token.StartsWith("[", StringComparison.Ordinal) Then
                Dim LinkMatch As Match = LinkPattern.Match(Token)
                If LinkMatch.Success Then
                    Dim LinkElement As New Hyperlink With {
                        .FontWeight = If(IsBold, FontWeights.Bold, FontWeights.Normal),
                        .FontStyle = If(IsItalic, FontStyles.Italic, FontStyles.Normal),
                        .Foreground = PtlGetBrush("ColorBrush3", Media.Brushes.SteelBlue),
                        .TextDecorations = TextDecorations.Underline
                    }
                    Dim ParsedUri As Uri = Nothing
                    If Uri.TryCreate(
                        LinkMatch.Groups("url").Value,
                        UriKind.Absolute, ParsedUri) Then
                        AddHandler LinkElement.RequestNavigate,
                            Sub(Sender, Args)
                                If Args.Uri IsNot Nothing Then
                                    OpenWebsite(Args.Uri.AbsoluteUri)
                                End If
                                Args.Handled = True
                            End Sub
                    End If
                    LinkElement.Inlines.Add(New Run(
                        LinkMatch.Groups("text").Value))
                    Target.Add(LinkElement)
                Else
                    PtlAddMarkdownText(Target, Token, IsBold, IsItalic)
                End If
            ElseIf Token.StartsWith("*", StringComparison.Ordinal) Then
                PtlRenderInlineMarkdown(
                    Token.Substring(1, Token.Length - 2),
                    Target, IsBold, True)
            ElseIf Token.StartsWith("_", StringComparison.Ordinal) Then
                PtlRenderInlineMarkdown(
                    Token.Substring(1, Token.Length - 2),
                    Target, IsBold, True)
            End If
            LastIndex = MatchValue.Index + MatchValue.Length
        Next
        If LastIndex < If(Text, "").Length Then
            PtlAddMarkdownText(
                Target, Text.Substring(LastIndex), IsBold, IsItalic)
        End If
    End Sub

    Private Sub PtlAddMarkdownText(
        Target As InlineCollection,
        Text As String,
        IsBold As Boolean,
        IsItalic As Boolean)
        If String.IsNullOrEmpty(Text) Then Return
        Dim TextParts As String() = Text.Replace(vbCrLf, vbLf).
            Replace(vbCr, vbLf).Split(vbLf)
        For Index As Integer = 0 To TextParts.Length - 1
            If Index > 0 Then Target.Add(New LineBreak())
            If TextParts(Index) = "" Then Continue For
            Target.Add(New Run(TextParts(Index)) With {
                .FontWeight = If(IsBold, FontWeights.Bold, FontWeights.Normal),
                .FontStyle = If(IsItalic, FontStyles.Italic, FontStyles.Normal)
            })
        Next
    End Sub

    Private Function PtlGetBrush(
        ResourceKey As String, Fallback As Media.Brush) As Media.Brush
        Try
            If Application.Current IsNot Nothing AndAlso
               Application.Current.Resources.Contains(ResourceKey) Then
                Return TryCast(Application.Current.Resources(ResourceKey), Media.Brush)
            End If
        Catch
        End Try
        Return Fallback
    End Function

End Module
