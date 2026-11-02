using System.Text;
using CppNet;

namespace Stride.Shaders.Parsing;

/// <summary>
/// A name a condition of the preprocessor read that was no macro at that point, so it counted as 0.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Directive">The directive of the condition, such as <c>if</c> or <c>ifdef</c>.</param>
/// <param name="Line">The line of the directive.</param>
/// <param name="Column">The column of the directive.</param>
public readonly record struct UndefinedConditionName(string Name, string Directive, int Line, int Column);

public static class MonoGamePreProcessor
{
    public static string OpenAndRun(string filepath, params ReadOnlySpan<(string Name, string Definition)> defines)
    {
        return Run(File.ReadAllText(filepath), Path.GetFileName(filepath), defines);
    }

    public static string Run(string content, string? filename, params ReadOnlySpan<(string Name, string Definition)> defines)
        => Run(content, filename, null, null, defines);

    public static string Run(string content, string? filename, Action<string>? onWarning, params ReadOnlySpan<(string Name, string Definition)> defines)
        => Run(content, filename, onWarning, null, defines);

    /// <summary>
    /// Runs the preprocessor on the code of a shader.
    /// </summary>
    /// <param name="onWarning">Receives each warning, in the form file(line,col): warning.</param>
    /// <param name="undefinedConditionNames">Collects each name a condition reads that is no macro at that point.</param>
    /// <exception cref="ShaderPreprocessorException">The code has a preprocessor error.</exception>
    public static string Run(string content, string? filename, Action<string>? onWarning, List<UndefinedConditionName>? undefinedConditionNames, params ReadOnlySpan<(string Name, string Definition)> defines)
    {
        var cpp = new Preprocessor();
        cpp.setListener(new DiagnosticListener(filename, onWarning));
        cpp.UndefinedConditionNames = undefinedConditionNames;
        cpp.addFeature(Feature.DIGRAPHS);
        cpp.addWarning(Warning.IMPORT);
        cpp.addFeature(Feature.INCLUDENEXT);
        // cpp.addFeature(Feature.LINEMARKERS);

        // Pass defines
        if (!defines.IsEmpty)
        {
            foreach (var (Name, Definition) in defines)
            {
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    cpp.addMacro(Name, Definition ?? string.Empty);
                }
            }
        }
        // Byte-order marks at the start (an editor can save a second one) are not code: spaces keep the columns
        var byteOrderMarks = content.Length - content.TrimStart('﻿').Length;
        if (byteOrderMarks > 0)
            content = new string(' ', byteOrderMarks) + content[byteOrderMarks..];

        // A directive must end with a newline for CppNet, but editors can save a last line without one
        if (!content.EndsWith('\n'))
            content += '\n';

        // Note: CppNet is compiled in this project but doesn't support nullable
        var inputSource = new StringLexerSource(content, true, filename!);

        cpp.addInput(inputSource);

        var textBuilder = new StringBuilder();

        var located = 0;
        var isEndOfStream = false;
        while (!isEndOfStream)
        {
            Token tok = cpp.token();
            // The output keeps the lines of the input, which the lexer's own positions don't always follow
            if (undefinedConditionNames != null && undefinedConditionNames.Count > located)
            {
                var line = textBuilder.ToString().AsSpan().Count('\n') + 1;
                var column = content.Split('\n').ElementAtOrDefault(line - 1)?.IndexOf('#') + 1 ?? 1;
                for (; located < undefinedConditionNames.Count; located++)
                    undefinedConditionNames[located] = undefinedConditionNames[located] with { Line = line, Column = column };
            }
            switch (tok.getType())
            {
                case Token.EOF:
                    isEndOfStream = true;
                    break;
                case Token.CCOMMENT:
                    var strComment = tok.getText() ?? string.Empty;
                    foreach (var commentChar in strComment)
                    {
                        textBuilder.Append(commentChar == '\n' ? '\n' : ' ');
                    }
                    break;
                case Token.CPPCOMMENT:
                    break;
                default:
                    var tokenText = tok.getText();
                    if (tokenText != null)
                    {
                        textBuilder.Append(tokenText);
                    }
                    break;
            }
        }
        return textBuilder.ToString();
    }

    private sealed class DiagnosticListener(string? filename, Action<string>? onWarning) : PreprocessorListener
    {
        public void handleWarning(Source source, int line, int column, string msg) => onWarning?.Invoke(Format(line, column, msg));

        public void handleError(Source source, int line, int column, string msg) => throw new ShaderPreprocessorException(Format(line, column, msg));

        public void handleSourceChange(Source source, string ev) { }

        // CppNet counts columns from 0
        private string Format(int line, int column, string msg) => $"{filename}({line},{column + 1}): {msg.TrimEnd()}";
    }
}
