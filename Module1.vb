Imports MySql.Data.MySqlClient
Imports System.IO

Module Module1
    Sub Main()
        GenerateClasses(
            My.MySettings.Default.MySqlConnectionString, My.MySettings.Default.PocoClassTargetPath)
    End Sub

    Public Sub GenerateClasses(connStr As String, outputDir As String)
        Using conn As New MySqlConnection(connStr)
            conn.Open()
            Console.WriteLine($"{connStr} opened using MySql.Data.9.4.0")

            ' Get all tables
            Console.WriteLine()
            Dim tables As New List(Of String)
            Using cmd As New MySqlCommand("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE()", conn)
                Console.WriteLine("Tables:")
                Using reader = cmd.ExecuteReader()
                    While reader.Read()
                        Dim Tab As String = reader.GetString(0)
                        Console.WriteLine(Tab)
                        tables.Add(reader.GetString(0))
                    End While
                End Using
            End Using

            ' Get all foreign key relationships
            Console.WriteLine()
            Dim relationships = GetRelationships(conn)

            ' Generate class for each table
            Console.WriteLine()
            Console.WriteLine($"{outputDir}:")
            For Each table In tables
                GenerateClass(conn, table, outputDir, relationships)
            Next
        End Using
    End Sub

    Private Function GetRelationships(conn As MySqlConnection) As Dictionary(Of String, List(Of Relationship))
        Console.WriteLine("Relations:")
        Dim relationships = New Dictionary(Of String, List(Of Relationship))

        Dim sql = "
            SELECT 
                TABLE_NAME, 
                COLUMN_NAME, 
                REFERENCED_TABLE_NAME, 
                REFERENCED_COLUMN_NAME
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE()
            AND REFERENCED_TABLE_NAME IS NOT NULL"

        Using cmd As New MySqlCommand(sql, conn)
            Using reader = cmd.ExecuteReader()
                While reader.Read()
                    Dim tableName = reader.GetString(0)
                    Dim relationship = New Relationship With {
                    .ColumnName = reader.GetString(1),
                    .ReferencedTable = reader.GetString(2),
                    .ReferencedColumn = reader.GetString(3)
                }

                    If Not relationships.ContainsKey(tableName) Then
                        relationships.Add(tableName, New List(Of Relationship))
                    End If
                    Console.WriteLine($"{tableName}:{relationship}")
                    relationships(tableName).Add(relationship)
                End While
            End Using
        End Using

        Return relationships
    End Function

    Private Sub GenerateClass(conn As MySqlConnection, tableName As String,
                        outputDir As String, relationships As Dictionary(Of String, List(Of Relationship)))
        Dim className = ToPascalCase(tableName)
        Dim sb As New Text.StringBuilder()
        sb.AppendLine($"Public Class {className}")
        sb.AppendLine()

        ' Get columns
        Using cmd As New MySqlCommand($"
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_KEY
            FROM INFORMATION_SCHEMA.COLUMNS 
            WHERE TABLE_SCHEMA = DATABASE() 
            AND TABLE_NAME = '{tableName}'
            ORDER BY ORDINAL_POSITION", conn)

            Using reader = cmd.ExecuteReader()
                While reader.Read()
                    Dim colName = reader.GetString(0)
                    Dim dataType = reader.GetString(1)
                    Dim isNullable = reader.GetString(2)
                    Dim isPrimary = reader.GetString(3) = "PRI"

                    ' Skip foreign key columns (we'll add navigation properties)
                    If relationships.ContainsKey(tableName) AndAlso
                   relationships(tableName).Any(Function(r) r.ColumnName = colName) Then
                        Continue While
                    End If

                    Dim vbType = GetVbType(dataType, isNullable)
                    sb.AppendLine($"    Public Property {ToPascalCase(colName)} As {vbType}")
                End While
            End Using
        End Using

        ' Add navigation properties
        If relationships.ContainsKey(tableName) Then
            sb.AppendLine()

            For Each rel In relationships(tableName)
                ' Check relationship type
                If IsOneToOne(conn, tableName, rel.ReferencedTable) Then
                    sb.AppendLine($"    Public Property {ToPascalCase(rel.ReferencedTable)} As {ToPascalCase(rel.ReferencedTable)}")
                Else
                    sb.AppendLine($"    Public Property {ToPascalCase(rel.ReferencedTable)}List As ICollection(Of {ToPascalCase(rel.ReferencedTable)})")
                End If
            Next
        End If

        sb.AppendLine("End Class")

        Directory.CreateDirectory(outputDir)
        File.WriteAllText(Path.Combine(outputDir, $"{className}.vb"), sb.ToString())
        Console.WriteLine(className)
    End Sub

    Private Function IsOneToOne(conn As MySqlConnection, tableName As String, referencedTable As String) As Boolean
        ' Check if this is a one-to-one relationship
        Using cmd As New MySqlCommand($"
            SELECT COUNT(*) 
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
            WHERE tc.TABLE_SCHEMA = DATABASE()
            AND tc.TABLE_NAME = '{referencedTable}'
            AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
            AND kcu.REFERENCED_TABLE_NAME = '{tableName}'", conn)

            Return Convert.ToInt32(cmd.ExecuteScalar()) > 0
        End Using
    End Function

    Private Function GetVbType(mySqlType As String, nullable As String) As String
        Dim typeMap As New Dictionary(Of String, String) From {
        {"int", "Integer"}, {"tinyint", "Boolean"}, {"smallint", "Short"},
        {"bigint", "Long"}, {"decimal", "Decimal"}, {"float", "Single"},
        {"double", "Double"}, {"char", "String"}, {"varchar", "String"},
        {"text", "String"}, {"datetime", "DateTime"}, {"timestamp", "DateTime"},
        {"date", "DateTime"}, {"time", "TimeSpan"}, {"year", "Integer"},
        {"bit", "Boolean"}, {"binary", "Byte()"}, {"varbinary", "Byte()"},
        {"blob", "Byte()"}, {"enum", "String"}, {"set", "String"}
    }

        Dim vbType As String = "Object"
        If typeMap.ContainsKey(mySqlType.ToLower()) Then
            vbType = typeMap(mySqlType.ToLower())
        End If

        ' Only make nullable if it's a value type and column is nullable
        If nullable = "YES" AndAlso IsValueType(vbType) Then
            Return $"Nullable(Of {vbType})"
        Else
            Return vbType
        End If
    End Function

    Private Function IsValueType(vbType As String) As Boolean
        Select Case vbType.ToLower()
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