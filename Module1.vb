Imports MySql.Data.MySqlClient
Imports System.IO
Imports System.Text

Module Module1
    Sub Main()
        Console.WriteLine("Starting POCO generation...")
        GenerateClasses(
            My.MySettings.Default.MySqlConnectionString,
            My.MySettings.Default.PocoClassTargetPath)
        Console.WriteLine("Generation complete!")
    End Sub

    Public Sub GenerateClasses(connStr As String, outputDir As String)
        Using conn As New MySqlConnection(connStr)
            conn.Open()
            Console.WriteLine($"Connected to: {conn.Database}")
            Console.WriteLine("Fetching tables...")

            ' Get all tables
            Dim tables As New List(Of String)
            Using cmd As New MySqlCommand("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE()", conn)
                Using reader = cmd.ExecuteReader()
                    While reader.Read()
                        Dim tableName = reader.GetString(0)
                        tables.Add(tableName)
                        Console.WriteLine($"Found table: {tableName}")
                    End While
                End Using
            End Using

            ' Get relationships
            Console.WriteLine("Analyzing relationships...")
            Dim relationships = GetRelationships(conn)

            ' Generate classes
            For Each table In tables
                Console.WriteLine($"Generating {table}...")
                GenerateClass(conn, table, outputDir, relationships)
            Next
        End Using
    End Sub

    Private Function GetRelationships(conn As MySqlConnection) As Dictionary(Of String, List(Of Relationship))
        Dim relationships = New Dictionary(Of String, List(Of Relationship))(StringComparer.OrdinalIgnoreCase)

        Dim sql = "
            SELECT 
                kcu.TABLE_NAME,
                kcu.COLUMN_NAME,
                kcu.REFERENCED_TABLE_NAME,
                kcu.REFERENCED_COLUMN_NAME
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
            WHERE kcu.TABLE_SCHEMA = DATABASE()
            AND kcu.REFERENCED_TABLE_NAME IS NOT NULL"

        Using cmd As New MySqlCommand(sql, conn)
            Using reader = cmd.ExecuteReader()
                While reader.Read()
                    Dim tableName = reader.GetString(0)
                    Dim colName = reader.GetString(1)
                    Dim refTable = reader.GetString(2)
                    Dim refCol = reader.GetString(3)

                    Console.WriteLine($"Relationship: {tableName}.{colName} → {refTable}.{refCol}")

                    If Not relationships.ContainsKey(tableName) Then
                        relationships.Add(tableName, New List(Of Relationship))
                    End If
                    relationships(tableName).Add(New Relationship With {
                        .ColumnName = colName,
                        .ReferencedTable = refTable,
                        .ReferencedColumn = refCol
                    })
                End While
            End Using
        End Using
        Return relationships
    End Function

    Private Sub GenerateClass(conn As MySqlConnection, tableName As String,
                             outputDir As String, relationships As Dictionary(Of String, List(Of Relationship)))
        Dim className = ToPascalCase(tableName)
        Dim sb As New StringBuilder()

        sb.AppendLine("' AUTO-GENERATED CLASS")
        sb.AppendLine("' Table: " & tableName)
        sb.AppendLine("' Generated: " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
        sb.AppendLine("Imports System.Collections.Generic")
        sb.AppendLine()
        sb.AppendLine($"Public Class {className}")
        sb.AppendLine()

        ' Track which columns are FKs so we don't duplicate
        Dim foreignKeyColumns As New List(Of String)
        If relationships.ContainsKey(tableName) Then
            foreignKeyColumns.AddRange(relationships(tableName).Select(Function(r) r.ColumnName))
        End If

        ' Generate all columns
        Using cmd As New MySqlCommand($"DESCRIBE `{tableName}`", conn)
            Using reader = cmd.ExecuteReader()
                While reader.Read()
                    Dim colName = reader.GetString(0)
                    Dim dataType = reader.GetString(1)
                    Dim isNullable = reader.GetString(2) = "YES"

                    ' Always include ALL columns, including FKs
                    Dim vbType = GetVbType(dataType, isNullable)
                    sb.AppendLine($"    Public Property {colName} As {vbType}")
                    Console.WriteLine($"  Added property: {colName} ({vbType})")
                End While
            End Using
        End Using

        ' Add navigation properties for relationships
        If relationships.ContainsKey(tableName) Then
            sb.AppendLine()
            For Each rel In relationships(tableName)
                Dim navPropertyName = ToPascalCase(rel.ReferencedTable)
                If Not IsOneToOne(conn, tableName, rel.ReferencedTable) Then
                    navPropertyName += "List"
                End If

                sb.AppendLine($"    Public Property {navPropertyName} As List(Of {ToPascalCase(rel.ReferencedTable)})")
                Console.WriteLine($"  Added navigation: {navPropertyName}")
            Next
        End If

        sb.AppendLine("End Class")

        ' Write to file
        Directory.CreateDirectory(outputDir)
        Dim filePath = Path.Combine(outputDir, $"{className}.vb")
        File.WriteAllText(filePath, sb.ToString())
        Console.WriteLine($"Saved to: {filePath}")
    End Sub

    Private Function IsOneToOne(conn As MySqlConnection, tableName As String, referencedTable As String) As Boolean
        ' Check if the referenced table has a FK back to this table
        Dim sql = "
            SELECT COUNT(*) 
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE 
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = @referencedTable
            AND REFERENCED_TABLE_NAME = @tableName"

        Using cmd As New MySqlCommand(sql, conn)
            cmd.Parameters.AddWithValue("@tableName", tableName)
            cmd.Parameters.AddWithValue("@referencedTable", referencedTable)
            Return Convert.ToInt32(cmd.ExecuteScalar()) > 0
        End Using
    End Function

    Private Function GetVbType(mySqlType As String, isNullable As Boolean) As String
        Dim typeMap As New Dictionary(Of String, String) From {
        {"int", "Integer"}, {"tinyint", "Boolean"}, {"smallint", "Short"},
        {"bigint", "Long"}, {"decimal", "Decimal"}, {"float", "Single"},
        {"double", "Double"}, {"char", "String"}, {"varchar", "String"},
        {"text", "String"}, {"datetime", "DateTime"}, {"timestamp", "DateTime"},
        {"date", "DateTime"}, {"time", "TimeSpan"}, {"year", "Integer"},
        {"bit", "Boolean"}, {"binary", "Byte()"}, {"varbinary", "Byte()"},
        {"blob", "Byte()"}, {"enum", "String"}, {"set", "String"}
    }

        ' SAFE type lookup - no invalid If operators
        Dim vbType As String = "Object"
        For Each kvp In typeMap
            If mySqlType.ToLower().Contains(kvp.Key.ToLower()) Then
                vbType = kvp.Value
                Exit For
            End If
        Next

        ' SAFE nullable handling
        If isNullable AndAlso IsValueType(vbType) Then
            Return $"Nullable(Of {vbType})"
        End If
        Return vbType
    End Function

    Private Function IsValueType(typeName As String) As Boolean
        Select Case typeName.ToLower()
            Case "integer", "boolean", "short", "long", "decimal", "single",
                 "double", "datetime", "timespan", "byte()"
                Return True
            Case Else
                Return False
        End Select
    End Function

    Private Function ToPascalCase(str As String) As String
        Dim culture = System.Globalization.CultureInfo.CurrentCulture
        Return culture.TextInfo.ToTitleCase(str.ToLower().Replace("_", " ")).Replace(" ", "")
    End Function
End Module

Public Class Relationship
    Public Property ColumnName As String
    Public Property ReferencedTable As String
    Public Property ReferencedColumn As String
End Class