Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.Globalization
Imports System.Text.RegularExpressions
Imports System.Windows.Media.Imaging

Public Class WarThunderSightPreviewResult
    Public Property Image As BitmapSource
    Public Property LineCount As Integer
    Public Property UnsupportedElements As String = ""
    Public Property Summary As String = ""
End Class

''' <summary>
''' 为战争雷霆 UserSights BLK 提供基础离线预览。
''' 当前重点还原 drawLines 线段、中央轴线和常用颜色设置。
''' </summary>
Public Module ModWarThunderSightPreview

    Private Const PreviewSize As Integer = 720
    Private Const DefaultBackgroundHex As String = "#A9BBC8"

    Private Class WarThunderSightLine
        Public Property X1 As Double
        Public Property Y1 As Double
        Public Property X2 As Double
        Public Property Y2 As Double
    End Class

    Private ReadOnly SightLineBlockRegex As New Regex(
        "\bline\s*\{(?<body>[^{}]*)\}",
        RegexOptions.IgnoreCase Or RegexOptions.Multiline Or RegexOptions.CultureInvariant)
    Private ReadOnly SightPoint4Regex As New Regex(
        "\bline\s*:\s*p4\s*=\s*(?<values>[^;}]+)",
        RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
    Private ReadOnly SightNumberRegex As New Regex(
        "[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?",
        RegexOptions.CultureInvariant)

    Public Function WarThunderFindSightDefinitionFiles(ComponentPath As String) As List(Of String)
        Dim Result As New List(Of String)
        If String.IsNullOrWhiteSpace(ComponentPath) Then Return Result
        If IO.File.Exists(ComponentPath) Then
            If ComponentPath.EndsWith(".blk", StringComparison.OrdinalIgnoreCase) Then
                Result.Add(IO.Path.GetFullPath(ComponentPath))
            End If
            Return Result
        End If
        If Not IO.Directory.Exists(ComponentPath) Then Return Result
        Try
            Result = IO.Directory.EnumerateFiles(
                ComponentPath, "*.blk", IO.SearchOption.AllDirectories).
                Where(Function(FilePath)
                          Return Not FilePath.Split(IO.Path.DirectorySeparatorChar).
                              Any(Function(Part)
                                      Return Part.Equals(
                                          ".ptl", StringComparison.OrdinalIgnoreCase)
                                  End Function)
                      End Function).
                Select(Function(FilePath) IO.Path.GetFullPath(FilePath)).
                OrderBy(Function(FilePath) FilePath, StringComparer.CurrentCultureIgnoreCase).
                ToList()
        Catch ex As Exception
            Logger.Warn(ex, $"扫描瞄具 BLK 文件失败：{ComponentPath}")
        End Try
        Return Result
    End Function

    Public ReadOnly Property WarThunderDefaultSightPreviewBackground As System.Drawing.Color
        Get
            Return WarThunderParseSightPreviewBackground(DefaultBackgroundHex)
        End Get
    End Property

    Public Function WarThunderParseSightPreviewBackground(
        Value As String) As System.Drawing.Color
        Dim Normalized As String = If(Value, "").Trim()
        If Regex.IsMatch(Normalized, "^#[0-9a-fA-F]{6}$") Then
            Return System.Drawing.Color.FromArgb(
                255,
                Convert.ToInt32(Normalized.Substring(1, 2), 16),
                Convert.ToInt32(Normalized.Substring(3, 2), 16),
                Convert.ToInt32(Normalized.Substring(5, 2), 16))
        End If
        Return System.Drawing.Color.FromArgb(255, 169, 187, 200)
    End Function

    Public Function WarThunderFormatSightPreviewBackground(
        Value As System.Drawing.Color) As String
        Return $"#{Value.R:X2}{Value.G:X2}{Value.B:X2}"
    End Function

    Public Function WarThunderRenderSightPreview(
        BlkPath As String) As WarThunderSightPreviewResult
        Return WarThunderRenderSightPreview(
            BlkPath, WarThunderDefaultSightPreviewBackground)
    End Function

    Public Function WarThunderRenderSightPreview(
        BlkPath As String,
        BackgroundColor As System.Drawing.Color) As WarThunderSightPreviewResult
        Dim Text As String = IO.File.ReadAllText(BlkPath)
        Dim Lines As New List(Of WarThunderSightLine)
        Dim ParsedLineCount As Integer = 0
        For Each BlockMatch As Match In SightLineBlockRegex.Matches(Text)
            Dim PointMatch As Match = SightPoint4Regex.Match(BlockMatch.Groups("body").Value)
            If Not PointMatch.Success Then Continue For
            ParsedLineCount += 1
            Dim Numbers As List(Of Double) = WarThunderParseSightNumbers(
                PointMatch.Groups("values").Value)
            If Numbers.Count < 4 Then Continue For
            Dim Values As Double() = Numbers.Take(4).ToArray()
            If Values.Any(Function(Value) Double.IsNaN(Value) OrElse Double.IsInfinity(Value)) Then
                Continue For
            End If
            Lines.Add(New WarThunderSightLine With {
                .X1 = Values(0),
                .Y1 = Values(1),
                .X2 = Values(2),
                .Y2 = Values(3)
            })
        Next

        Dim Result As New WarThunderSightPreviewResult With {
            .LineCount = Lines.Count,
            .UnsupportedElements = WarThunderFindUnsupportedSightElements(Text)
        }
        If ParsedLineCount = 0 Then
            Result.Summary = "这个瞄具没有可识别的 line:p4 线段定义。"
        ElseIf Lines.Count = 0 Then
            Result.Summary = "瞄具中的线段数据无法解析。"
        Else
            Result.Summary = $"已还原 {Lines.Count} 条线段。"
        End If
        If Result.UnsupportedElements <> "" Then
            Result.Summary &= " 部分 " & Result.UnsupportedElements & " 元素暂未显示。"
        End If

        Dim StrokeColor As System.Drawing.Color =
            WarThunderChooseSightStrokeColor(Text, BackgroundColor)
        Dim LineSizeMultiplier As Double = WarThunderReadSightNumber(
            Text, "\blineSizeMult\s*:\s*r\s*=\s*(?<value>[^\r\n;]+)", 1.2)
        Result.Image = WarThunderCreateSightPreviewBitmap(
            Lines, BackgroundColor, StrokeColor, LineSizeMultiplier)
        Return Result
    End Function

    Private Function WarThunderParseSightNumbers(Value As String) As List(Of Double)
        Dim Result As New List(Of Double)
        For Each NumberMatch As Match In SightNumberRegex.Matches(Value)
            Dim Parsed As Double
            If Double.TryParse(NumberMatch.Value, NumberStyles.Float,
                               CultureInfo.InvariantCulture, Parsed) Then
                Result.Add(Parsed)
            End If
        Next
        Return Result
    End Function

    Private Function WarThunderFindUnsupportedSightElements(Text As String) As String
        Dim Result As New List(Of String)
        If Regex.IsMatch(Text, "\bdrawTexts\s*\{", RegexOptions.IgnoreCase) Then
            Result.Add("文字")
        End If
        If Regex.IsMatch(Text, "\bdrawCircles\s*\{", RegexOptions.IgnoreCase) Then
            Result.Add("圆环")
        End If
        If Regex.IsMatch(Text, "\bdrawQuads\s*\{", RegexOptions.IgnoreCase) Then
            Result.Add("贴图")
        End If
        If Regex.IsMatch(Text, "\bdrawImages\s*\{", RegexOptions.IgnoreCase) Then
            Result.Add("图片")
        End If
        Return String.Join("、", Result)
    End Function

    Private Function WarThunderChooseSightStrokeColor(
        Text As String,
        BackgroundColor As System.Drawing.Color) As System.Drawing.Color
        Dim PrimaryColor As System.Drawing.Color =
            WarThunderReadSightColor(Text, "crosshairColor")
        Dim LightColor As System.Drawing.Color =
            WarThunderReadSightColor(Text, "crosshairLightColor")
        Dim StrokeColor As System.Drawing.Color = If(
            LightColor.A > 0 AndAlso
                (CInt(LightColor.R) + CInt(LightColor.G) + CInt(LightColor.B)) > 45,
            LightColor, PrimaryColor)
        Dim BackgroundBrightness As Double =
            WarThunderColorBrightness(BackgroundColor)
        If StrokeColor.A = 0 OrElse
           (CInt(StrokeColor.R) + CInt(StrokeColor.G) + CInt(StrokeColor.B)) < 45 OrElse
           Math.Abs(WarThunderColorBrightness(StrokeColor) - BackgroundBrightness) < 72 Then
            StrokeColor = If(BackgroundBrightness > 135,
                System.Drawing.Color.FromArgb(255, 28, 36, 45),
                System.Drawing.Color.FromArgb(255, 242, 246, 250))
        End If
        If StrokeColor.A < 170 Then
            StrokeColor = System.Drawing.Color.FromArgb(
                238, StrokeColor.R, StrokeColor.G, StrokeColor.B)
        End If
        Return StrokeColor
    End Function

    Private Function WarThunderColorBrightness(
        Value As System.Drawing.Color) As Double
        Return 0.299 * Value.R + 0.587 * Value.G + 0.114 * Value.B
    End Function

    Private Function WarThunderReadSightColor(
        Text As String, PropertyName As String) As System.Drawing.Color
        Dim MatchValue As Match = Regex.Match(
            Text, "\b" & Regex.Escape(PropertyName) &
                "\s*:\s*c\s*=\s*(?<value>[^\r\n;]+)",
            RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
        If Not MatchValue.Success Then Return System.Drawing.Color.Transparent
        Dim Numbers As List(Of Double) =
            WarThunderParseSightNumbers(MatchValue.Groups("value").Value)
        If Numbers.Count < 3 Then Return System.Drawing.Color.Transparent
        Dim Red As Integer = WarThunderClampColor(Numbers(0))
        Dim Green As Integer = WarThunderClampColor(Numbers(1))
        Dim Blue As Integer = WarThunderClampColor(Numbers(2))
        Dim Alpha As Integer = If(Numbers.Count >= 4,
            WarThunderClampColor(Numbers(3)), 255)
        Return System.Drawing.Color.FromArgb(Alpha, Red, Green, Blue)
    End Function

    Private Function WarThunderClampColor(Value As Double) As Integer
        Return Math.Max(0, Math.Min(255, CInt(Math.Round(Value))))
    End Function

    Private Function WarThunderReadSightNumber(
        Text As String, Pattern As String, DefaultValue As Double) As Double
        Dim MatchValue As Match = Regex.Match(
            Text, Pattern, RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
        If Not MatchValue.Success Then Return DefaultValue
        Dim Parsed As Double
        If Double.TryParse(MatchValue.Groups("value").Value.Trim(),
                           NumberStyles.Float, CultureInfo.InvariantCulture, Parsed) Then
            Return Parsed
        End If
        Return DefaultValue
    End Function

    Private Function WarThunderCreateSightPreviewBitmap(
        Lines As List(Of WarThunderSightLine),
        BackgroundColor As System.Drawing.Color,
        StrokeColor As System.Drawing.Color,
        LineSizeMultiplier As Double) As BitmapSource
        Using Bitmap As New System.Drawing.Bitmap(
            PreviewSize, PreviewSize, PixelFormat.Format32bppPArgb)
            Using Graphics As System.Drawing.Graphics =
                System.Drawing.Graphics.FromImage(Bitmap)
                Graphics.SmoothingMode = SmoothingMode.AntiAlias
                Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality
                Graphics.Clear(System.Drawing.Color.FromArgb(
                    255, BackgroundColor.R, BackgroundColor.G, BackgroundColor.B))

                Dim IsLightBackground As Boolean =
                    WarThunderColorBrightness(BackgroundColor) > 135
                Dim GridPen As New System.Drawing.Pen(
                    If(IsLightBackground,
                        System.Drawing.Color.FromArgb(55, 35, 52, 68),
                        System.Drawing.Color.FromArgb(55, 190, 210, 230)), 1)
                Try
                    For Index As Integer = 1 To 3
                        Dim Position As Single = PreviewSize * Index / 4.0F
                        Graphics.DrawLine(GridPen, Position, 0, Position, PreviewSize)
                        Graphics.DrawLine(GridPen, 0, Position, PreviewSize, Position)
                    Next
                Finally
                    GridPen.Dispose()
                End Try

                If Lines.Count > 0 Then
                    Dim MinX As Double = Lines.Min(Function(Line) Math.Min(Line.X1, Line.X2))
                    Dim MaxX As Double = Lines.Max(Function(Line) Math.Max(Line.X1, Line.X2))
                    Dim MinY As Double = Lines.Min(Function(Line) Math.Min(Line.Y1, Line.Y2))
                    Dim MaxY As Double = Lines.Max(Function(Line) Math.Max(Line.Y1, Line.Y2))
                    Dim RangeX As Double = Math.Max(0.001, MaxX - MinX)
                    Dim RangeY As Double = Math.Max(0.001, MaxY - MinY)
                    Dim ContentSize As Double = PreviewSize - 92
                    Dim ScaleValue As Double = ContentSize / Math.Max(RangeX, RangeY)
                    Dim CenterX As Single = PreviewSize / 2.0F
                    Dim CenterY As Single = PreviewSize / 2.0F
                    Dim MidX As Double = (MinX + MaxX) / 2
                    Dim MidY As Double = (MinY + MaxY) / 2

                    Dim PreviewPen As New System.Drawing.Pen(
                        StrokeColor, CSng(Math.Max(1.15,
                            Math.Min(4.5, 1.15 * LineSizeMultiplier))))
                    Try
                        For Each Line In Lines
                            Graphics.DrawLine(
                                PreviewPen,
                                WarThunderSightPointX(Line.X1, MidX, ScaleValue, CenterX),
                                WarThunderSightPointY(Line.Y1, MidY, ScaleValue, CenterY),
                                WarThunderSightPointX(Line.X2, MidX, ScaleValue, CenterX),
                                WarThunderSightPointY(Line.Y2, MidY, ScaleValue, CenterY))
                        Next
                    Finally
                        PreviewPen.Dispose()
                    End Try

                    Dim AxisPen As New System.Drawing.Pen(
                        If(IsLightBackground,
                            System.Drawing.Color.FromArgb(105, 35, 55, 75),
                            System.Drawing.Color.FromArgb(105, 220, 235, 250)), 1) With {
                            .DashStyle = DashStyle.Dot
                        }
                    Try
                        Graphics.DrawLine(AxisPen, 24, CenterY, PreviewSize - 24, CenterY)
                        Graphics.DrawLine(AxisPen, CenterX, 24, CenterX, PreviewSize - 24)
                    Finally
                        AxisPen.Dispose()
                    End Try
                Else
                    Using MessageFont As New System.Drawing.Font(
                        "Microsoft YaHei UI", 15, System.Drawing.FontStyle.Regular,
                        System.Drawing.GraphicsUnit.Pixel)
                        Using MessageBrush As New System.Drawing.SolidBrush(
                            If(IsLightBackground,
                                System.Drawing.Color.FromArgb(220, 35, 48, 60),
                                System.Drawing.Color.FromArgb(220, 235, 242, 250)))
                            Dim MessageText As String = "暂无可显示的瞄具线段"
                            Dim TextSize As System.Drawing.SizeF =
                                Graphics.MeasureString(MessageText, MessageFont)
                            Graphics.DrawString(
                                MessageText, MessageFont, MessageBrush,
                                (PreviewSize - TextSize.Width) / 2,
                                (PreviewSize - TextSize.Height) / 2)
                        End Using
                    End Using
                End If
            End Using

            Using Stream As New IO.MemoryStream()
                Bitmap.Save(Stream, ImageFormat.Png)
                Stream.Position = 0
                Dim Image As New BitmapImage()
                Image.BeginInit()
                Image.CacheOption = BitmapCacheOption.OnLoad
                Image.StreamSource = Stream
                Image.EndInit()
                Image.Freeze()
                Return Image
            End Using
        End Using
    End Function

    Private Function WarThunderSightPointX(
        Value As Double, MidValue As Double, ScaleValue As Double,
        CenterValue As Single) As Single
        Return CSng(CenterValue + (Value - MidValue) * ScaleValue)
    End Function

    Private Function WarThunderSightPointY(
        Value As Double, MidValue As Double, ScaleValue As Double,
        CenterValue As Single) As Single
        Return CSng(CenterValue + (Value - MidValue) * ScaleValue)
    End Function

End Module
