// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage.Internal;

namespace Microsoft.EntityFrameworkCore.Query;

/// <summary>
///     <para>
///         A query SQL generator to get <see cref="IRelationalCommand" /> for given <see cref="SelectExpression" />.
///     </para>
///     <para>
///         This type is typically used by database providers (and other extensions). It is generally
///         not used in application code.
///     </para>
/// </summary>
public class QuerySqlGenerator : SqlExpressionVisitor
{
    private static readonly Dictionary<ExpressionType, string> OperatorMap = new()
    {
        { ExpressionType.Equal, " = " },
        { ExpressionType.NotEqual, " <> " },
        { ExpressionType.GreaterThan, " > " },
        { ExpressionType.GreaterThanOrEqual, " >= " },
        { ExpressionType.LessThan, " < " },
        { ExpressionType.LessThanOrEqual, " <= " },
        { ExpressionType.AndAlso, " AND " },
        { ExpressionType.OrElse, " OR " },
        { ExpressionType.Add, " + " },
        { ExpressionType.Subtract, " - " },
        { ExpressionType.Multiply, " * " },
        { ExpressionType.Divide, " / " },
        { ExpressionType.Modulo, " % " },
        { ExpressionType.And, " & " },
        { ExpressionType.Or, " | " }
    };

    private readonly IRelationalCommandBuilderFactory _relationalCommandBuilderFactory;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private IRelationalCommandBuilder _relationalCommandBuilder;
    private Dictionary<string, int>? _repeatedParameterCounts;

    /// <summary>
    ///     Creates a new instance of the <see cref="QuerySqlGenerator" /> class.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this class.</param>
    public QuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
    {
        Dependencies = dependencies;

        _relationalCommandBuilderFactory = dependencies.RelationalCommandBuilderFactory;
        _sqlGenerationHelper = dependencies.SqlGenerationHelper;
        _relationalCommandBuilder = default!;
    }

    /// <summary>
    ///     Relational provider-specific dependencies for this service.
    /// </summary>
    protected virtual QuerySqlGeneratorDependencies Dependencies { get; }

    /// <summary>
    ///     Gets a relational command for a query expression.
    /// </summary>
    /// <param name="queryExpression">A query expression to print in command text.</param>
    /// <returns>A relational command with a SQL represented by the query expression.</returns>
    public virtual IRelationalCommand GetCommand(Expression queryExpression)
    {
        _relationalCommandBuilder = _relationalCommandBuilderFactory.Create();

        GenerateRootCommand(queryExpression);

        return _relationalCommandBuilder.Build();
    }

    /// <summary>
    ///     Generates the command for the given top-level query expression. This allows providers to intercept if an expression
    ///     requires different processing when it is at top-level.
    /// </summary>
    /// <param name="queryExpression">A query expression to print in command.</param>
    protected virtual void GenerateRootCommand(Expression queryExpression)
    {
        switch (queryExpression)
        {
            case SelectExpression selectExpression:
                GenerateTagsHeaderComment(selectExpression.Tags);

                if (selectExpression.IsNonComposedFromSql())
                {
                    GenerateFromSql((FromSqlExpression)selectExpression.Tables[0]);
                }
                else
                {
                    VisitSelect(selectExpression);
                }

                break;

            case UpdateExpression updateExpression:
                GenerateTagsHeaderComment(updateExpression.Tags);
                VisitUpdate(updateExpression);
                break;

            case DeleteExpression deleteExpression:
                GenerateTagsHeaderComment(deleteExpression.Tags);
                VisitDelete(deleteExpression);
                break;

            default:
                base.Visit(queryExpression);
                break;
        }
    }

    /// <summary>
    ///     The default alias separator.
    /// </summary>
    protected virtual string AliasSeparator
        => " AS ";

    /// <summary>
    ///     The current SQL command builder.
    /// </summary>
    protected virtual IRelationalCommandBuilder Sql
        => _relationalCommandBuilder;

    /// <summary>
    ///     Generates the head comment for tags.
    /// </summary>
    /// <param name="selectExpression">A select expression to generate tags for.</param>
    [Obsolete("Use the method which takes tags instead.")]
    protected virtual void GenerateTagsHeaderComment(SelectExpression selectExpression)
    {
        if (selectExpression.Tags.Count > 0)
        {
            foreach (var tag in selectExpression.Tags)
            {
                _relationalCommandBuilder.AppendLines(_sqlGenerationHelper.GenerateComment(tag));
            }

            _relationalCommandBuilder.AppendLine();
        }
    }

    /// <summary>
    ///     Generates the head comment for tags.
    /// </summary>
    /// <param name="tags">A set of tags to print as comment.</param>
    protected virtual void GenerateTagsHeaderComment(ISet<string> tags)
    {
        if (tags.Count > 0)
        {
            foreach (var tag in tags)
            {
                _relationalCommandBuilder.AppendLines(_sqlGenerationHelper.GenerateComment(tag));
            }

            _relationalCommandBuilder.AppendLine();
        }
    }

    /// <inheritdoc />
    protected override Expression VisitSqlFragment(SqlFragmentExpression sqlFragmentExpression)
    {
        _relationalCommandBuilder.Append(sqlFragmentExpression.Sql);

        return sqlFragmentExpression;
    }

    private static bool IsNonComposedSetOperation(SelectExpression selectExpression)
        => selectExpression.Offset == null
            && selectExpression.Limit == null
            && !selectExpression.IsDistinct
            && selectExpression.Predicate == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Tables.Count == 1
            && selectExpression.Tables[0] is SetOperationBase setOperation
            && selectExpression.Projection.Count == setOperation.Source1.Projection.Count
            && selectExpression.Projection.Select(
                    (pe, index) => pe.Expression is ColumnExpression column
                        && string.Equals(column.TableAlias, setOperation.Alias, StringComparison.Ordinal)
                        && string.Equals(
                            column.Name, setOperation.Source1.Projection[index].Alias, StringComparison.Ordinal))
                .All(e => e);

    /// <inheritdoc />
    protected override Expression VisitDelete(DeleteExpression deleteExpression)
    {
        var selectExpression = deleteExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Limit == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Tables.Count == 1
            && selectExpression.Tables[0] == deleteExpression.Table
            && selectExpression.Projection.Count == 0)
        {
            _relationalCommandBuilder.Append("DELETE FROM ");
            Visit(deleteExpression.Table);

            if (selectExpression.Predicate != null)
            {
                _relationalCommandBuilder.AppendLine().Append("WHERE ");
                Visit(selectExpression.Predicate);
            }

            return deleteExpression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(nameof(RelationalQueryableExtensions.ExecuteDelete)));
    }

    /// <inheritdoc />
    protected override Expression VisitSelect(SelectExpression selectExpression)
    {
        IDisposable? subQueryIndent = null;
        if (selectExpression.Alias != null)
        {
            _relationalCommandBuilder.AppendLine("(");
            subQueryIndent = _relationalCommandBuilder.Indent();
        }

        if (IsNonComposedSetOperation(selectExpression))
        {
            // Naked set operation
            GenerateSetOperation((SetOperationBase)selectExpression.Tables[0]);
        }
        else
        {
            _relationalCommandBuilder.Append("SELECT ");

            if (selectExpression.IsDistinct)
            {
                _relationalCommandBuilder.Append("DISTINCT ");
            }

            GenerateTop(selectExpression);

            if (selectExpression.Projection.Any())
            {
                GenerateList(selectExpression.Projection, e => Visit(e));
            }
            else
            {
                _relationalCommandBuilder.Append("1");
            }

            if (selectExpression.Tables.Any())
            {
                _relationalCommandBuilder.AppendLine().Append("FROM ");

                GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());
            }
            else
            {
                GeneratePseudoFromClause();
            }

            if (selectExpression.Predicate != null)
            {
                _relationalCommandBuilder.AppendLine().Append("WHERE ");

                Visit(selectExpression.Predicate);
            }

            if (selectExpression.GroupBy.Count > 0)
            {
                _relationalCommandBuilder.AppendLine().Append("GROUP BY ");

                GenerateList(selectExpression.GroupBy, e => Visit(e));
            }

            if (selectExpression.Having != null)
            {
                _relationalCommandBuilder.AppendLine().Append("HAVING ");

                Visit(selectExpression.Having);
            }

            GenerateOrderings(selectExpression);
            GenerateLimitOffset(selectExpression);
        }

        if (selectExpression.Alias != null)
        {
            subQueryIndent!.Dispose();

            _relationalCommandBuilder.AppendLine()
                .Append(")")
                .Append(AliasSeparator)
                .Append(_sqlGenerationHelper.DelimitIdentifier(selectExpression.Alias));
        }

        return selectExpression;
    }

    /// <summary>
    ///     Generates a pseudo FROM clause. Required by some providers when a query has no actual FROM clause.
    /// </summary>
    protected virtual void GeneratePseudoFromClause()
    {
    }

    /// <inheritdoc />
    protected override Expression VisitProjection(ProjectionExpression projectionExpression)
    {
        Visit(projectionExpression.Expression);

        if (projectionExpression.Alias != string.Empty
            && !(projectionExpression.Expression is ColumnExpression column && column.Name == projectionExpression.Alias))
        {
            _relationalCommandBuilder
                .Append(AliasSeparator)
                .Append(_sqlGenerationHelper.DelimitIdentifier(projectionExpression.Alias));
        }

        return projectionExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitSqlFunction(SqlFunctionExpression sqlFunctionExpression)
    {
        if (sqlFunctionExpression.IsBuiltIn)
        {
            if (sqlFunctionExpression.Instance != null)
            {
                Visit(sqlFunctionExpression.Instance);
                _relationalCommandBuilder.Append(".");
            }

            _relationalCommandBuilder.Append(sqlFunctionExpression.Name);
        }
        else
        {
            if (!string.IsNullOrEmpty(sqlFunctionExpression.Schema))
            {
                _relationalCommandBuilder
                    .Append(_sqlGenerationHelper.DelimitIdentifier(sqlFunctionExpression.Schema))
                    .Append(".");
            }

            _relationalCommandBuilder
                .Append(_sqlGenerationHelper.DelimitIdentifier(sqlFunctionExpression.Name));
        }

        if (!sqlFunctionExpression.IsNiladic)
        {
            _relationalCommandBuilder.Append("(");
            GenerateList(sqlFunctionExpression.Arguments, e => Visit(e));
            _relationalCommandBuilder.Append(")");
        }

        return sqlFunctionExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitTableValuedFunction(TableValuedFunctionExpression tableValuedFunctionExpression)
    {
        if (!string.IsNullOrEmpty(tableValuedFunctionExpression.StoreFunction.Schema))
        {
            _relationalCommandBuilder
                .Append(_sqlGenerationHelper.DelimitIdentifier(tableValuedFunctionExpression.StoreFunction.Schema))
                .Append(".");
        }

        var name = tableValuedFunctionExpression.StoreFunction.IsBuiltIn
            ? tableValuedFunctionExpression.StoreFunction.Name
            : _sqlGenerationHelper.DelimitIdentifier(tableValuedFunctionExpression.StoreFunction.Name);

        _relationalCommandBuilder
            .Append(name)
            .Append("(");

        GenerateList(tableValuedFunctionExpression.Arguments, e => Visit(e));

        _relationalCommandBuilder
            .Append(")")
            .Append(AliasSeparator)
            .Append(_sqlGenerationHelper.DelimitIdentifier(tableValuedFunctionExpression.Alias));

        return tableValuedFunctionExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitColumn(ColumnExpression columnExpression)
    {
        _relationalCommandBuilder
            .Append(_sqlGenerationHelper.DelimitIdentifier(columnExpression.TableAlias))
            .Append(".")
            .Append(_sqlGenerationHelper.DelimitIdentifier(columnExpression.Name));

        return columnExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitTable(TableExpression tableExpression)
    {
        _relationalCommandBuilder
            .Append(_sqlGenerationHelper.DelimitIdentifier(tableExpression.Name, tableExpression.Schema))
            .Append(AliasSeparator)
            .Append(_sqlGenerationHelper.DelimitIdentifier(tableExpression.Alias));

        return tableExpression;
    }

    private void GenerateFromSql(FromSqlExpression fromSqlExpression)
    {
        var sql = fromSqlExpression.Sql;
        string[]? substitutions;

        switch (fromSqlExpression.Arguments)
        {
            case ConstantExpression { Value: CompositeRelationalParameter compositeRelationalParameter }:
            {
                var subParameters = compositeRelationalParameter.RelationalParameters;
                substitutions = new string[subParameters.Count];
                for (var i = 0; i < subParameters.Count; i++)
                {
                    substitutions[i] = _sqlGenerationHelper.GenerateParameterNamePlaceholder(subParameters[i].InvariantName);
                }

                _relationalCommandBuilder.AddParameter(compositeRelationalParameter);

                break;
            }

            case ConstantExpression { Value: object[] constantValues }:
            {
                substitutions = new string[constantValues.Length];
                for (var i = 0; i < constantValues.Length; i++)
                {
                    var value = constantValues[i];
                    if (value is RawRelationalParameter rawRelationalParameter)
                    {
                        substitutions[i] = _sqlGenerationHelper.GenerateParameterNamePlaceholder(rawRelationalParameter.InvariantName);
                        _relationalCommandBuilder.AddParameter(rawRelationalParameter);
                    }
                    else if (value is SqlConstantExpression sqlConstantExpression)
                    {
                        substitutions[i] = sqlConstantExpression.TypeMapping!.GenerateSqlLiteral(sqlConstantExpression.Value);
                    }
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(fromSqlExpression),
                    fromSqlExpression.Arguments,
                    RelationalStrings.InvalidFromSqlArguments(
                        fromSqlExpression.Arguments.GetType(),
                        fromSqlExpression.Arguments is ConstantExpression constantExpression
                            ? constantExpression.Value?.GetType()
                            : null));
        }

        // ReSharper disable once CoVariantArrayConversion
        // InvariantCulture not needed since substitutions are all strings
        sql = string.Format(sql, substitutions);

        _relationalCommandBuilder.AppendLines(sql);
    }

    /// <inheritdoc />
    protected override Expression VisitFromSql(FromSqlExpression fromSqlExpression)
    {
        _relationalCommandBuilder.AppendLine("(");

        CheckComposableSql(fromSqlExpression.Sql);

        using (_relationalCommandBuilder.Indent())
        {
            GenerateFromSql(fromSqlExpression);
        }

        _relationalCommandBuilder.Append(")")
            .Append(AliasSeparator)
            .Append(_sqlGenerationHelper.DelimitIdentifier(fromSqlExpression.Alias));

        return fromSqlExpression;
    }

    /// <summary>
    ///     Checks whether a given SQL string is composable, i.e. can be embedded as a subquery within a
    ///     larger SQL query.
    /// </summary>
    /// <param name="sql">An SQL string to be checked for composability.</param>
    /// <exception cref="InvalidOperationException">The given SQL isn't composable.</exception>
    protected virtual void CheckComposableSql(string sql)
    {
        var span = sql.AsSpan().TrimStart();

        while (true)
        {
            // SQL -- comment
            if (span.StartsWith("--"))
            {
                var i = span.IndexOf('\n');
                span = i > 0
                    ? span[(i + 1)..].TrimStart()
                    : throw new InvalidOperationException(RelationalStrings.FromSqlNonComposable);
                continue;
            }

            // SQL /* */ comment
            if (span.StartsWith("/*"))
            {
                var i = span.IndexOf("*/");
                span = i > 0
                    ? span[(i + 2)..].TrimStart()
                    : throw new InvalidOperationException(RelationalStrings.FromSqlNonComposable);
                continue;
            }

            break;
        }

        CheckComposableSqlTrimmed(span);
    }

    /// <summary>
    ///     Checks whether a given SQL string is composable, i.e. can be embedded as a subquery within a
    ///     larger SQL query. The provided <paramref name="sql" /> is already trimmed for whitespace and comments.
    /// </summary>
    /// <param name="sql">An trimmed SQL string to be checked for composability.</param>
    /// <exception cref="InvalidOperationException">The given SQL isn't composable.</exception>
    protected virtual void CheckComposableSqlTrimmed(ReadOnlySpan<char> sql)
    {
        sql = sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            ? sql["SELECT".Length..]
            : sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
                ? sql["WITH".Length..]
                : throw new InvalidOperationException(RelationalStrings.FromSqlNonComposable);

        if (sql.Length > 0
            && (char.IsWhiteSpace(sql[0]) || sql.StartsWith("--") || sql.StartsWith("/*")))
        {
            return;
        }

        throw new InvalidOperationException(RelationalStrings.FromSqlNonComposable);
    }

    /// <inheritdoc />
    protected override Expression VisitSqlBinary(SqlBinaryExpression sqlBinaryExpression)
    {
        var requiresBrackets = RequiresParentheses(sqlBinaryExpression, sqlBinaryExpression.Left);

        if (requiresBrackets)
        {
            _relationalCommandBuilder.Append("(");
        }

        Visit(sqlBinaryExpression.Left);

        if (requiresBrackets)
        {
            _relationalCommandBuilder.Append(")");
        }

        _relationalCommandBuilder.Append(GetOperator(sqlBinaryExpression));

        requiresBrackets = RequiresParentheses(sqlBinaryExpression, sqlBinaryExpression.Right);

        if (requiresBrackets)
        {
            _relationalCommandBuilder.Append("(");
        }

        Visit(sqlBinaryExpression.Right);

        if (requiresBrackets)
        {
            _relationalCommandBuilder.Append(")");
        }

        return sqlBinaryExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitSqlConstant(SqlConstantExpression sqlConstantExpression)
    {
        _relationalCommandBuilder
            .Append(sqlConstantExpression.TypeMapping!.GenerateSqlLiteral(sqlConstantExpression.Value));

        return sqlConstantExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitSqlParameter(SqlParameterExpression sqlParameterExpression)
    {
        var invariantName = sqlParameterExpression.Name;
        var parameterName = sqlParameterExpression.Name;

        if (_relationalCommandBuilder.Parameters
            .All(
                p => p.InvariantName != parameterName
                    || (p is TypeMappedRelationalParameter typeMappedRelationalParameter
                        && (typeMappedRelationalParameter.RelationalTypeMapping.StoreType != sqlParameterExpression.TypeMapping!.StoreType
                            || typeMappedRelationalParameter.RelationalTypeMapping.Converter
                            != sqlParameterExpression.TypeMapping!.Converter))))
        {
            parameterName = GetUniqueParameterName(parameterName);
            _relationalCommandBuilder.AddParameter(
                invariantName,
                _sqlGenerationHelper.GenerateParameterName(parameterName),
                sqlParameterExpression.TypeMapping!,
                sqlParameterExpression.IsNullable);
        }

        _relationalCommandBuilder
            .Append(_sqlGenerationHelper.GenerateParameterNamePlaceholder(parameterName));

        return sqlParameterExpression;

        string GetUniqueParameterName(string currentName)
        {
            _repeatedParameterCounts ??= new Dictionary<string, int>();

            if (!_repeatedParameterCounts.TryGetValue(currentName, out var currentCount))
            {
                _repeatedParameterCounts[currentName] = 0;

                return currentName;
            }

            currentCount++;
            _repeatedParameterCounts[currentName] = currentCount;

            return currentName + "_" + currentCount;
        }
    }

    /// <inheritdoc />
    protected override Expression VisitOrdering(OrderingExpression orderingExpression)
    {
        if (orderingExpression.Expression is SqlConstantExpression
            || orderingExpression.Expression is SqlParameterExpression)
        {
            _relationalCommandBuilder.Append("(SELECT 1)");
        }
        else
        {
            Visit(orderingExpression.Expression);
        }

        if (!orderingExpression.IsAscending)
        {
            _relationalCommandBuilder.Append(" DESC");
        }

        return orderingExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitLike(LikeExpression likeExpression)
    {
        Visit(likeExpression.Match);
        _relationalCommandBuilder.Append(" LIKE ");
        Visit(likeExpression.Pattern);

        if (likeExpression.EscapeChar != null)
        {
            _relationalCommandBuilder.Append(" ESCAPE ");
            Visit(likeExpression.EscapeChar);
        }

        return likeExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitCollate(CollateExpression collateExpression)
    {
        Visit(collateExpression.Operand);

        _relationalCommandBuilder
            .Append(" COLLATE ")
            .Append(collateExpression.Collation);

        return collateExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitDistinct(DistinctExpression distinctExpression)
    {
        _relationalCommandBuilder.Append("DISTINCT (");
        Visit(distinctExpression.Operand);
        _relationalCommandBuilder.Append(")");

        return distinctExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitCase(CaseExpression caseExpression)
    {
        _relationalCommandBuilder.Append("CASE");

        if (caseExpression.Operand != null)
        {
            _relationalCommandBuilder.Append(" ");
            Visit(caseExpression.Operand);
        }

        using (_relationalCommandBuilder.Indent())
        {
            foreach (var whenClause in caseExpression.WhenClauses)
            {
                _relationalCommandBuilder
                    .AppendLine()
                    .Append("WHEN ");
                Visit(whenClause.Test);
                _relationalCommandBuilder.Append(" THEN ");
                Visit(whenClause.Result);
            }

            if (caseExpression.ElseResult != null)
            {
                _relationalCommandBuilder
                    .AppendLine()
                    .Append("ELSE ");
                Visit(caseExpression.ElseResult);
            }
        }

        _relationalCommandBuilder
            .AppendLine()
            .Append("END");

        return caseExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitSqlUnary(SqlUnaryExpression sqlUnaryExpression)
    {
        switch (sqlUnaryExpression.OperatorType)
        {
            case ExpressionType.Convert:
            {
                _relationalCommandBuilder.Append("CAST(");
                var requiresBrackets = RequiresParentheses(sqlUnaryExpression, sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append("(");
                }

                Visit(sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append(")");
                }

                _relationalCommandBuilder.Append(" AS ");
                _relationalCommandBuilder.Append(sqlUnaryExpression.TypeMapping!.StoreType);
                _relationalCommandBuilder.Append(")");
                break;
            }

            case ExpressionType.Not
                when sqlUnaryExpression.Type == typeof(bool):
            {
                _relationalCommandBuilder.Append("NOT (");
                Visit(sqlUnaryExpression.Operand);
                _relationalCommandBuilder.Append(")");
                break;
            }

            case ExpressionType.Not:
            {
                _relationalCommandBuilder.Append("~");

                var requiresBrackets = RequiresParentheses(sqlUnaryExpression, sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append("(");
                }

                Visit(sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append(")");
                }

                break;
            }

            case ExpressionType.Equal:
            {
                var requiresBrackets = RequiresParentheses(sqlUnaryExpression, sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append("(");
                }

                Visit(sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append(")");
                }

                _relationalCommandBuilder.Append(" IS NULL");
                break;
            }

            case ExpressionType.NotEqual:
            {
                var requiresBrackets = RequiresParentheses(sqlUnaryExpression, sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append("(");
                }

                Visit(sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append(")");
                }

                _relationalCommandBuilder.Append(" IS NOT NULL");
                break;
            }

            case ExpressionType.Negate:
            {
                _relationalCommandBuilder.Append("-");
                var requiresBrackets = RequiresParentheses(sqlUnaryExpression, sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append("(");
                }

                Visit(sqlUnaryExpression.Operand);
                if (requiresBrackets)
                {
                    _relationalCommandBuilder.Append(")");
                }

                break;
            }
        }

        return sqlUnaryExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitExists(ExistsExpression existsExpression)
    {
        if (existsExpression.IsNegated)
        {
            _relationalCommandBuilder.Append("NOT ");
        }

        _relationalCommandBuilder.AppendLine("EXISTS (");

        using (_relationalCommandBuilder.Indent())
        {
            Visit(existsExpression.Subquery);
        }

        _relationalCommandBuilder.Append(")");

        return existsExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitIn(InExpression inExpression)
    {
        if (inExpression.Values != null)
        {
            Visit(inExpression.Item);
            _relationalCommandBuilder.Append(inExpression.IsNegated ? " NOT IN " : " IN ");
            _relationalCommandBuilder.Append("(");
            var valuesConstant = (SqlConstantExpression)inExpression.Values;
            var valuesList = ((IEnumerable<object?>)valuesConstant.Value!)
                .Select(v => new SqlConstantExpression(Expression.Constant(v), valuesConstant.TypeMapping)).ToList();
            GenerateList(valuesList, e => Visit(e));
            _relationalCommandBuilder.Append(")");
        }
        else
        {
            Visit(inExpression.Item);
            _relationalCommandBuilder.Append(inExpression.IsNegated ? " NOT IN " : " IN ");
            _relationalCommandBuilder.AppendLine("(");

            using (_relationalCommandBuilder.Indent())
            {
                Visit(inExpression.Subquery);
            }

            _relationalCommandBuilder.AppendLine().Append(")");
        }

        return inExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitAtTimeZone(AtTimeZoneExpression atTimeZoneExpression)
    {
        var requiresBrackets = RequiresParentheses(atTimeZoneExpression, atTimeZoneExpression.Operand);

        if (requiresBrackets)
        {
            _relationalCommandBuilder.Append("(");
        }

        Visit(atTimeZoneExpression.Operand);

        if (requiresBrackets)
        {
            _relationalCommandBuilder.Append(")");
        }

        _relationalCommandBuilder.Append(" AT TIME ZONE ");

        requiresBrackets = RequiresParentheses(atTimeZoneExpression, atTimeZoneExpression.TimeZone);

        if (requiresBrackets)
        {
            _relationalCommandBuilder.Append("(");
        }

        Visit(atTimeZoneExpression.TimeZone);

        if (requiresBrackets)
        {
            _relationalCommandBuilder.Append(")");
        }

        return atTimeZoneExpression;
    }

    /// <summary>
    ///     Gets a SQL operator for a SQL binary operation.
    /// </summary>
    /// <param name="binaryExpression">A SQL binary operation.</param>
    /// <returns>A string representation of the binary operator.</returns>
    protected virtual string GetOperator(SqlBinaryExpression binaryExpression)
        => OperatorMap[binaryExpression.OperatorType];

    /// <summary>
    ///     Returns a bool value indicating if the inner SQL expression required to be put inside parenthesis
    ///     when generating SQL for outer SQL expression.
    /// </summary>
    /// <param name="outerExpression">The outer expression which provides context in which SQL is being generated.</param>
    /// <param name="innerExpression">The inner expression which may need to be put inside parenthesis.</param>
    /// <returns>A bool value indicating that parenthesis is required or not. </returns>
    protected virtual bool RequiresParentheses(SqlExpression outerExpression, SqlExpression innerExpression)
    {
        switch (innerExpression)
        {
            case AtTimeZoneExpression or LikeExpression:
                return true;

            case SqlUnaryExpression sqlUnaryExpression:
            {
                // Wrap IS (NOT) NULL operation when applied on bool column.
                if ((sqlUnaryExpression.OperatorType == ExpressionType.Equal
                        || sqlUnaryExpression.OperatorType == ExpressionType.NotEqual)
                    && sqlUnaryExpression.Operand.Type == typeof(bool))
                {
                    return true;
                }

                if (sqlUnaryExpression.OperatorType == ExpressionType.Negate
                    && outerExpression is SqlUnaryExpression { OperatorType: ExpressionType.Negate })
                {
                    // double negative sign is interpreted as a comment in SQL, so we need to enclose it in brackets
                    return true;
                }

                return false;
            }

            case SqlBinaryExpression sqlBinaryExpression:
            {
                if (outerExpression is SqlBinaryExpression outerBinary)
                {
                    // Math, bitwise, comparison and equality operators have higher precedence
                    if (outerBinary.OperatorType == ExpressionType.AndAlso)
                    {
                        return sqlBinaryExpression.OperatorType == ExpressionType.OrElse;
                    }

                    if (outerBinary.OperatorType == ExpressionType.OrElse)
                    {
                        // Precedence-wise AND is above OR but we still add parenthesis for ease of understanding
                        return sqlBinaryExpression.OperatorType == ExpressionType.AndAlso;
                    }
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Generates a TOP construct in the relational command
    /// </summary>
    /// <param name="selectExpression">A select expression to use.</param>
    protected virtual void GenerateTop(SelectExpression selectExpression)
    {
    }

    /// <summary>
    ///     Generates an ORDER BY clause in the relational command
    /// </summary>
    /// <param name="selectExpression">A select expression to use.</param>
    protected virtual void GenerateOrderings(SelectExpression selectExpression)
    {
        if (selectExpression.Orderings.Any())
        {
            var orderings = selectExpression.Orderings.ToList();

            if (selectExpression.Limit == null
                && selectExpression.Offset == null)
            {
                orderings.RemoveAll(oe => oe.Expression is SqlConstantExpression || oe.Expression is SqlParameterExpression);
            }

            if (orderings.Count > 0)
            {
                _relationalCommandBuilder.AppendLine()
                    .Append("ORDER BY ");

                GenerateList(orderings, e => Visit(e));
            }
        }
    }

    /// <summary>
    ///     Generates a LIMIT...OFFSET... construct in the relational command
    /// </summary>
    /// <param name="selectExpression">A select expression to use.</param>
    protected virtual void GenerateLimitOffset(SelectExpression selectExpression)
    {
        if (selectExpression.Offset != null)
        {
            _relationalCommandBuilder.AppendLine()
                .Append("OFFSET ");

            Visit(selectExpression.Offset);

            _relationalCommandBuilder.Append(" ROWS");

            if (selectExpression.Limit != null)
            {
                _relationalCommandBuilder.Append(" FETCH NEXT ");

                Visit(selectExpression.Limit);

                _relationalCommandBuilder.Append(" ROWS ONLY");
            }
        }
        else if (selectExpression.Limit != null)
        {
            _relationalCommandBuilder.AppendLine()
                .Append("FETCH FIRST ");

            Visit(selectExpression.Limit);

            _relationalCommandBuilder.Append(" ROWS ONLY");
        }
    }

    private void GenerateList<T>(
        IReadOnlyList<T> items,
        Action<T> generationAction,
        Action<IRelationalCommandBuilder>? joinAction = null)
    {
        joinAction ??= (isb => isb.Append(", "));

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                joinAction(_relationalCommandBuilder);
            }

            generationAction(items[i]);
        }
    }

    /// <inheritdoc />
    protected override Expression VisitCrossJoin(CrossJoinExpression crossJoinExpression)
    {
        _relationalCommandBuilder.Append("CROSS JOIN ");
        Visit(crossJoinExpression.Table);

        return crossJoinExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitCrossApply(CrossApplyExpression crossApplyExpression)
    {
        _relationalCommandBuilder.Append("CROSS APPLY ");
        Visit(crossApplyExpression.Table);

        return crossApplyExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitOuterApply(OuterApplyExpression outerApplyExpression)
    {
        _relationalCommandBuilder.Append("OUTER APPLY ");
        Visit(outerApplyExpression.Table);

        return outerApplyExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitInnerJoin(InnerJoinExpression innerJoinExpression)
    {
        _relationalCommandBuilder.Append("INNER JOIN ");
        Visit(innerJoinExpression.Table);
        _relationalCommandBuilder.Append(" ON ");
        Visit(innerJoinExpression.JoinPredicate);

        return innerJoinExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitLeftJoin(LeftJoinExpression leftJoinExpression)
    {
        _relationalCommandBuilder.Append("LEFT JOIN ");
        Visit(leftJoinExpression.Table);
        _relationalCommandBuilder.Append(" ON ");
        Visit(leftJoinExpression.JoinPredicate);

        return leftJoinExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitScalarSubquery(ScalarSubqueryExpression scalarSubqueryExpression)
    {
        _relationalCommandBuilder.AppendLine("(");
        using (_relationalCommandBuilder.Indent())
        {
            Visit(scalarSubqueryExpression.Subquery);
        }

        _relationalCommandBuilder.Append(")");

        return scalarSubqueryExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitRowNumber(RowNumberExpression rowNumberExpression)
    {
        _relationalCommandBuilder.Append("ROW_NUMBER() OVER(");
        if (rowNumberExpression.Partitions.Any())
        {
            _relationalCommandBuilder.Append("PARTITION BY ");
            GenerateList(rowNumberExpression.Partitions, e => Visit(e));
            _relationalCommandBuilder.Append(" ");
        }

        _relationalCommandBuilder.Append("ORDER BY ");
        GenerateList(rowNumberExpression.Orderings, e => Visit(e));
        _relationalCommandBuilder.Append(")");

        return rowNumberExpression;
    }

    /// <summary>
    ///     Generates a set operation in the relational command.
    /// </summary>
    /// <param name="setOperation">A set operation to print.</param>
    protected virtual void GenerateSetOperation(SetOperationBase setOperation)
    {
        GenerateSetOperationOperand(setOperation, setOperation.Source1);
        _relationalCommandBuilder
            .AppendLine()
            .Append(GetSetOperation(setOperation))
            .AppendLine(setOperation.IsDistinct ? string.Empty : " ALL");
        GenerateSetOperationOperand(setOperation, setOperation.Source2);

        static string GetSetOperation(SetOperationBase operation)
            => operation switch
            {
                ExceptExpression => "EXCEPT",
                IntersectExpression => "INTERSECT",
                UnionExpression => "UNION",
                _ => throw new InvalidOperationException(CoreStrings.UnknownEntity("SetOperationType"))
            };
    }

    /// <summary>
    ///     Generates an operand for a given set operation in the relational command.
    /// </summary>
    /// <param name="setOperation">A set operation to use.</param>
    /// <param name="operand">A set operation operand to print.</param>
    protected virtual void GenerateSetOperationOperand(SetOperationBase setOperation, SelectExpression operand)
    {
        // INTERSECT has higher precedence over UNION and EXCEPT, but otherwise evaluation is left-to-right.
        // To preserve meaning, add parentheses whenever a set operation is nested within a different set operation.
        if (IsNonComposedSetOperation(operand)
            && operand.Tables[0].GetType() != setOperation.GetType())
        {
            _relationalCommandBuilder.AppendLine("(");
            using (_relationalCommandBuilder.Indent())
            {
                Visit(operand);
            }

            _relationalCommandBuilder.AppendLine().Append(")");
        }
        else
        {
            Visit(operand);
        }
    }

    private void GenerateSetOperationHelper(SetOperationBase setOperation)
    {
        _relationalCommandBuilder.AppendLine("(");
        using (_relationalCommandBuilder.Indent())
        {
            GenerateSetOperation(setOperation);
        }

        _relationalCommandBuilder.AppendLine()
            .Append(")")
            .Append(AliasSeparator)
            .Append(_sqlGenerationHelper.DelimitIdentifier(setOperation.Alias));
    }

    /// <inheritdoc />
    protected override Expression VisitExcept(ExceptExpression exceptExpression)
    {
        GenerateSetOperationHelper(exceptExpression);

        return exceptExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitIntersect(IntersectExpression intersectExpression)
    {
        GenerateSetOperationHelper(intersectExpression);

        return intersectExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitUnion(UnionExpression unionExpression)
    {
        GenerateSetOperationHelper(unionExpression);

        return unionExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitUpdate(UpdateExpression updateExpression)
    {
        var selectExpression = updateExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Limit == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Projection.Count == 0
            && (selectExpression.Tables.Count == 1
                || !ReferenceEquals(selectExpression.Tables[0], updateExpression.Table)
                || selectExpression.Tables[1] is InnerJoinExpression
                || selectExpression.Tables[1] is CrossJoinExpression))
        {
            _relationalCommandBuilder.Append("UPDATE ");
            Visit(updateExpression.Table);
            _relationalCommandBuilder.AppendLine();
            _relationalCommandBuilder.Append("SET ");
            _relationalCommandBuilder.Append(
                $"{_sqlGenerationHelper.DelimitIdentifier(updateExpression.ColumnValueSetters[0].Column.Name)} = ");
            Visit(updateExpression.ColumnValueSetters[0].Value);
            using (_relationalCommandBuilder.Indent())
            {
                foreach (var columnValueSetter in updateExpression.ColumnValueSetters.Skip(1))
                {
                    _relationalCommandBuilder.AppendLine(",");
                    _relationalCommandBuilder.Append($"{_sqlGenerationHelper.DelimitIdentifier(columnValueSetter.Column.Name)} = ");
                    Visit(columnValueSetter.Value);
                }
            }

            var predicate = selectExpression.Predicate;
            var firstTablePrinted = false;
            if (selectExpression.Tables.Count > 1)
            {
                _relationalCommandBuilder.AppendLine().Append("FROM ");
                for (var i = 0; i < selectExpression.Tables.Count; i++)
                {
                    var table = selectExpression.Tables[i];
                    var joinExpression = table as JoinExpressionBase;

                    if (ReferenceEquals(updateExpression.Table, joinExpression?.Table ?? table))
                    {
                        LiftPredicate(table);
                        continue;
                    }

                    if (firstTablePrinted)
                    {
                        _relationalCommandBuilder.AppendLine();
                    }
                    else
                    {
                        firstTablePrinted = true;
                        LiftPredicate(table);
                        table = joinExpression?.Table ?? table;
                    }

                    Visit(table);

                    void LiftPredicate(TableExpressionBase joinTable)
                    {
                        if (joinTable is PredicateJoinExpressionBase predicateJoinExpression)
                        {
                            predicate = predicate == null
                                ? predicateJoinExpression.JoinPredicate
                                : new SqlBinaryExpression(
                                    ExpressionType.AndAlso,
                                    predicateJoinExpression.JoinPredicate,
                                    predicate,
                                    typeof(bool),
                                    predicate.TypeMapping);
                        }
                    }
                }
            }

            if (predicate != null)
            {
                _relationalCommandBuilder.AppendLine().Append("WHERE ");
                Visit(predicate);
            }

            return updateExpression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(nameof(RelationalQueryableExtensions.ExecuteUpdate)));
    }

    /// <inheritdoc />
    protected override Expression VisitJsonScalar(JsonScalarExpression jsonScalarExpression)
        => throw new InvalidOperationException(
            RelationalStrings.JsonNodeMustBeHandledByProviderSpecificVisitor);
}
