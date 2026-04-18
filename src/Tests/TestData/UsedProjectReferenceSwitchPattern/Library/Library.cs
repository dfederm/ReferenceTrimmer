namespace Library
{
    public static class Bar
    {
        // Dependency.Foo used only in switch expression type pattern (ISwitchExpressionArmOperation)
        public static string CategorizeExpr(object obj) => obj switch
        {
            Dependency.Foo => "foo",
            _ => "other"
        };

        // Dependency.Foo used only in switch case clause pattern (IPatternCaseClauseOperation)
        public static string CategorizeStmt(object obj)
        {
            switch (obj)
            {
                case Dependency.Foo _: return "foo";
                default: return "other";
            }
        }
    }
}
