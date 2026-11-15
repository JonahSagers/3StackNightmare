using System.Collections.Generic;
using NUnit.Framework;

// Runs every RegressionSuite check as its own NUnit test, so they also show up in
// Window > General > Test Runner > EditMode. The checks themselves live in
// Assets/Scripts/Regression/RegressionSuite.cs (plain C#, so they also run in Play mode).
public class GameRulesTests
{
    static IEnumerable<string> Checks() => RegressionSuite.Names();

    [TestCaseSource(nameof(Checks))]
    public void Rule(string check)
    {
        string failure = RegressionSuite.Run(check);
        Assert.IsNull(failure, failure);
    }
}
