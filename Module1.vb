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
            Console.WriteLine("Fetching tables and views...")

            ' Get all tables AND views
            Dim tables As New List(Of TableInfo)
            Using cmd As New MySqlCommand("SELECT TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE()", conn)
                Using reader = cmd.ExecuteReader()
                    While reader.Read()
                        Dim tableName = reader.GetString(0)
                        Dim tableType = reader.GetString(1)
                        tables.Add(New TableInfo With {
                            .Name = tableName,
                            .IsView = (tableType = "VIEW")
                        })
                        Console.WriteLine($"Found {(If(tableType = "VIEW", "view", "table"))}: {tableName}")
                    End While
                End Using
            End Using

            ' Get relationships (only for tables, not views)
            Console.WriteLine("Analyzing relationships...")
            Dim relationships = GetRelationships(conn)

            ' Generate classes
            For Each table In tables
                Console.WriteLine($"Generating {table.Name}...")
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

    Private Sub GenerateClass(conn As MySqlConnection, tableInfo As TableInfo,
                             outputDir As String, relationships As Dictionary(Of String, List(Of Relationship)))
        Dim className = ToPascalCase(tableInfo.Name)
        Dim sb As New StringBuilder()

        sb.AppendLine("' AUTO-GENERATED CLASS")
        sb.AppendLine($"' {(If(tableInfo.IsView, "View", "Table"))}: {tableInfo.Name}")
        sb.AppendLine("' Generated: " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
        sb.AppendLine("Imports System.Collections.Generic")
        sb.AppendLine()
        sb.AppendLine($"Public Class {className}")
        sb.AppendLine()

        ' Track which columns are FKs so we don't duplicate
        Dim foreignKeyColumns As New List(Of String)
        If Not tableInfo.IsView AndAlso relationships.ContainsKey(tableInfo.Name) Then
            foreignKeyColumns.AddRange(relationships(tableInfo.Name).Select(Function(r) r.ColumnName))
        End If

        ' Generate all columns
        Using cmd As New MySqlCommand($"DESCRIBE `{tableInfo.Name}`", conn)
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

        ' Add navigation properties for relationships (only for tables, not views)
        If Not tableInfo.IsView AndAlso relationships.ContainsKey(tableInfo.Name) Then
            sb.AppendLine()
            For Each rel In relationships(tableInfo.Name)
                Dim navPropertyName = ToPascalCase(rel.ReferencedTable)
                If Not IsOneToOne(conn, tableInfo.Name, rel.ReferencedTable) Then
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

    Private Function GetVbType(dataType As String, isNullable As Boolean) As String
        ' Normalize the data type by removing length/precision
        Dim baseType = dataType.Split("("c)(0).ToLower()

        Dim vbType As String = "Object" ' Default fallback

        Select Case baseType
            Case "tinyint"
                ' Special handling for tinyint(1) which MySQL uses as boolean
                If dataType.ToLower().StartsWith("tinyint(1)") Then
                    vbType = "Boolean"
                Else
                    vbType = "Byte"
                End If

            Case "smallint"
                vbType = "Short"
            Case "int", "integer", "mediumint"
                vbType = "Integer"
            Case "bigint"
                vbType = "Long"
            Case "decimal", "numeric", "dec"
                vbType = "Decimal"
            Case "float"
                vbType = "Single"
            Case "double"
                vbType = "Double"
            Case "bit"
                vbType = "Boolean"
            Case "date", "year"
                vbType = "Date"
            Case "datetime", "timestamp", "time"
                vbType = "DateTime"
            Case "char", "varchar", "text", "tinytext", "mediumtext", "longtext", "enum", "set"
                vbType = "String"
            Case "binary", "varbinary", "blob", "tinyblob", "mediumblob", "longblob"
                vbType = "Byte()"
        End Select

        ' Handle nullable types
        If isNullable AndAlso vbType <> "String" AndAlso vbType <> "Object" AndAlso vbType <> "Byte()" Then
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

Public Class TableInfo
    Public Property Name As String
    Public Property IsView As Boolean
End Class