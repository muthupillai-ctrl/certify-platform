using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddTemplateVisualDesignFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccentColor",
                schema: "unresolved",
                table: "certificate_templates",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FontFamily",
                schema: "unresolved",
                table: "certificate_templates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LayoutJson",
                schema: "unresolved",
                table: "certificate_templates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SubtitleText",
                schema: "unresolved",
                table: "certificate_templates",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TitleText",
                schema: "unresolved",
                table: "certificate_templates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TemplateAccentColor",
                schema: "unresolved",
                table: "achievements",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateFontFamily",
                schema: "unresolved",
                table: "achievements",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateLayoutJson",
                schema: "unresolved",
                table: "achievements",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TemplateSubtitleText",
                schema: "unresolved",
                table: "achievements",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateTitleText",
                schema: "unresolved",
                table: "achievements",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            // Backfill — every certificate_templates/achievements row that
            // existed before this migration gets TitleText/TemplateTitleText
            // set to its own Name/Title, and LayoutJson/TemplateLayoutJson
            // set to a layout matching EXACTLY what
            // Validation.DefaultLayouts.Generate(kind, fieldSchemaJson)
            // would produce for that row's own Kind and own (already
            // pre-existing) field schema — this PL/pgSQL loop is a
            // deliberate line-for-line port of that C# method, field
            // ordering and y-position increments included, not just a
            // fixed shape. This is what makes the "old credential renders
            // identically" checkpoint hold even for achievements that
            // already had custom fields (e.g. the seeded Marks Card
            // template's rollNumber/subjects) before this feature existed
            // — their fields keep their same relative on-page order.
            migrationBuilder.Sql(@"
DO $do$
DECLARE
  tpl RECORD;
  fld RECORD;
  elements TEXT;
  field_y INT;
  footer_y INT;
  canvas_height INT;
BEGIN
  FOR tpl IN SELECT ""Id"", ""Name"" AS title_text, ""Kind"" AS kind, ""FieldSchemaJson"" AS schema_json FROM unresolved.certificate_templates LOOP
    elements := '';
    field_y := 380;
    FOR fld IN SELECT value FROM jsonb_array_elements(tpl.schema_json::jsonb) LOOP
      IF (fld.value->>'type') = 'table' THEN
        elements := elements || format('{""id"":""field_%s"",""type"":""table"",""fieldKey"":""%s"",""x"":60,""y"":%s,""width"":580,""height"":200,""fontSize"":12,""color"":""#111827""},',
          fld.value->>'key', fld.value->>'key', field_y);
        field_y := field_y + 220;
      ELSE
        elements := elements || format('{""id"":""field_%s"",""type"":""field"",""fieldKey"":""%s"",""x"":60,""y"":%s,""width"":260,""fontSize"":13,""align"":""left"",""color"":""#111827""},',
          fld.value->>'key', fld.value->>'key', field_y);
        field_y := field_y + 36;
      END IF;
    END LOOP;
    footer_y := GREATEST(field_y + 40, 480);
    canvas_height := footer_y + 120;

    UPDATE unresolved.certificate_templates SET
      ""TitleText"" = tpl.title_text,
      ""LayoutJson"" =
        '{""canvasWidth"":700,""canvasHeight"":' || canvas_height || ',""backgroundColor"":""#fdfbf5"",""elements"":[' ||
        '{""id"":""institutionName"",""type"":""institutionName"",""x"":60,""y"":24,""width"":580,""fontSize"":11,""align"":""center"",""color"":""#6b7280""},' ||
        '{""id"":""logo"",""type"":""logo"",""x"":300,""y"":60,""width"":100,""height"":60},' ||
        '{""id"":""title"",""type"":""title"",""x"":60,""y"":140,""width"":580,""fontSize"":32,""align"":""center"",""color"":""#111827"",""bold"":true},' ||
        '{""id"":""subtitle"",""type"":""subtitle"",""x"":60,""y"":182,""width"":580,""fontSize"":14,""align"":""center"",""color"":""#6b7280""},' ||
        '{""id"":""recipientName"",""type"":""recipientName"",""x"":60,""y"":240,""width"":580,""fontSize"":28,""align"":""center"",""color"":""#111827""},' ||
        '{""id"":""achievementTitle"",""type"":""achievementTitle"",""x"":60,""y"":300,""width"":580,""fontSize"":18,""align"":""center"",""color"":""#111827""},' ||
        (CASE WHEN tpl.kind = 0 THEN '{""id"":""achievementDescription"",""type"":""achievementDescription"",""x"":110,""y"":336,""width"":480,""fontSize"":12,""align"":""center"",""color"":""#4b5563""},' ELSE '' END) ||
        elements ||
        '{""id"":""issuedDate"",""type"":""issuedDate"",""x"":60,""y"":' || footer_y || ',""width"":200,""fontSize"":11,""align"":""left"",""color"":""#6b7280""},' ||
        '{""id"":""qrCode"",""type"":""qrCode"",""x"":300,""y"":' || (footer_y - 20) || ',""width"":84,""height"":84},' ||
        '{""id"":""credentialId"",""type"":""credentialId"",""x"":420,""y"":' || footer_y || ',""width"":220,""fontSize"":10,""align"":""right"",""color"":""#6b7280""}' ||
        ']}'
    WHERE ""Id"" = tpl.""Id"";
  END LOOP;

  FOR tpl IN SELECT ""Id"", ""Title"" AS title_text, ""TemplateKind"" AS kind, ""FieldSchemaJson"" AS schema_json FROM unresolved.achievements LOOP
    elements := '';
    field_y := 380;
    FOR fld IN SELECT value FROM jsonb_array_elements(tpl.schema_json::jsonb) LOOP
      IF (fld.value->>'type') = 'table' THEN
        elements := elements || format('{""id"":""field_%s"",""type"":""table"",""fieldKey"":""%s"",""x"":60,""y"":%s,""width"":580,""height"":200,""fontSize"":12,""color"":""#111827""},',
          fld.value->>'key', fld.value->>'key', field_y);
        field_y := field_y + 220;
      ELSE
        elements := elements || format('{""id"":""field_%s"",""type"":""field"",""fieldKey"":""%s"",""x"":60,""y"":%s,""width"":260,""fontSize"":13,""align"":""left"",""color"":""#111827""},',
          fld.value->>'key', fld.value->>'key', field_y);
        field_y := field_y + 36;
      END IF;
    END LOOP;
    footer_y := GREATEST(field_y + 40, 480);
    canvas_height := footer_y + 120;

    UPDATE unresolved.achievements SET
      ""TemplateTitleText"" = tpl.title_text,
      ""TemplateLayoutJson"" =
        '{""canvasWidth"":700,""canvasHeight"":' || canvas_height || ',""backgroundColor"":""#fdfbf5"",""elements"":[' ||
        '{""id"":""institutionName"",""type"":""institutionName"",""x"":60,""y"":24,""width"":580,""fontSize"":11,""align"":""center"",""color"":""#6b7280""},' ||
        '{""id"":""logo"",""type"":""logo"",""x"":300,""y"":60,""width"":100,""height"":60},' ||
        '{""id"":""title"",""type"":""title"",""x"":60,""y"":140,""width"":580,""fontSize"":32,""align"":""center"",""color"":""#111827"",""bold"":true},' ||
        '{""id"":""subtitle"",""type"":""subtitle"",""x"":60,""y"":182,""width"":580,""fontSize"":14,""align"":""center"",""color"":""#6b7280""},' ||
        '{""id"":""recipientName"",""type"":""recipientName"",""x"":60,""y"":240,""width"":580,""fontSize"":28,""align"":""center"",""color"":""#111827""},' ||
        '{""id"":""achievementTitle"",""type"":""achievementTitle"",""x"":60,""y"":300,""width"":580,""fontSize"":18,""align"":""center"",""color"":""#111827""},' ||
        (CASE WHEN tpl.kind = 0 THEN '{""id"":""achievementDescription"",""type"":""achievementDescription"",""x"":110,""y"":336,""width"":480,""fontSize"":12,""align"":""center"",""color"":""#4b5563""},' ELSE '' END) ||
        elements ||
        '{""id"":""issuedDate"",""type"":""issuedDate"",""x"":60,""y"":' || footer_y || ',""width"":200,""fontSize"":11,""align"":""left"",""color"":""#6b7280""},' ||
        '{""id"":""qrCode"",""type"":""qrCode"",""x"":300,""y"":' || (footer_y - 20) || ',""width"":84,""height"":84},' ||
        '{""id"":""credentialId"",""type"":""credentialId"",""x"":420,""y"":' || footer_y || ',""width"":220,""fontSize"":10,""align"":""right"",""color"":""#6b7280""}' ||
        ']}'
    WHERE ""Id"" = tpl.""Id"";
  END LOOP;
END
$do$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccentColor",
                schema: "unresolved",
                table: "certificate_templates");

            migrationBuilder.DropColumn(
                name: "FontFamily",
                schema: "unresolved",
                table: "certificate_templates");

            migrationBuilder.DropColumn(
                name: "LayoutJson",
                schema: "unresolved",
                table: "certificate_templates");

            migrationBuilder.DropColumn(
                name: "SubtitleText",
                schema: "unresolved",
                table: "certificate_templates");

            migrationBuilder.DropColumn(
                name: "TitleText",
                schema: "unresolved",
                table: "certificate_templates");

            migrationBuilder.DropColumn(
                name: "TemplateAccentColor",
                schema: "unresolved",
                table: "achievements");

            migrationBuilder.DropColumn(
                name: "TemplateFontFamily",
                schema: "unresolved",
                table: "achievements");

            migrationBuilder.DropColumn(
                name: "TemplateLayoutJson",
                schema: "unresolved",
                table: "achievements");

            migrationBuilder.DropColumn(
                name: "TemplateSubtitleText",
                schema: "unresolved",
                table: "achievements");

            migrationBuilder.DropColumn(
                name: "TemplateTitleText",
                schema: "unresolved",
                table: "achievements");
        }
    }
}
