/*
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at https://mozilla.org/MPL/2.0/.
*/

namespace Underanalyzer.Compiler.Errors;

/// <summary>
/// Represents an error produced by the compiler.
/// </summary>
public interface ICompileError
{
    /// <summary>
    /// A simple, but possibly non-user-friendly error message.
    /// </summary>
    public string BaseMessage { get; }

    /// <summary>
    /// Generates a full, user-friendly error message based on the contents of this compile error.
    /// </summary>
    /// <returns>Generated message</returns>
    public string GenerateMessage();

    /// <summary>
    /// Attempts to retrieve the position of this error in the source text it originated from.
    /// Line and column numbers are one-indexed, and width is measured in characters.
    /// </summary>
    /// <param name="line">One-indexed line number of the error.</param>
    /// <param name="column">One-indexed column number of the error.</param>
    /// <param name="width">Width of the error in characters (at least 1).</param>
    /// <returns>Whether a position could be determined for this error.</returns>
    public bool TryGetPosition(out int line, out int column, out int width);
}
