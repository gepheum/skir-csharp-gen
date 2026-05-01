import type { RecordKey, RecordLocation, ResolvedType } from "skir-internal";
import { getTypeName, modulePathToNamespace } from "./naming.js";

/**
 * Transforms a type found in a `.skir` file into a C# type expression.
 */
export class TypeSpeller {
  constructor(
    readonly recordMap: ReadonlyMap<RecordKey, RecordLocation>,
    readonly modulePath: string,
  ) {}

  getCsharpType(type: ResolvedType): string {
    switch (type.kind) {
      case "record": {
        const recordLocation = this.recordMap.get(type.key)!;
        const ns = modulePathToNamespace(recordLocation.modulePath);
        const className = getTypeName(recordLocation);
        return `global::${ns}.${className}`;
      }
      case "array": {
        const itemType = this.getCsharpType(type.item);
        return `global::System.Collections.Generic.IReadOnlyList<${itemType}>`;
      }
      case "optional": {
        const otherType = this.getCsharpType(type.other);
        return `${otherType}?`;
      }
      case "primitive": {
        const { primitive } = type;
        switch (primitive) {
          case "bool":
            return "bool";
          case "int32":
            return "int";
          case "int64":
            return "long";
          case "hash64":
            return "ulong";
          case "float32":
            return "float";
          case "float64":
            return "double";
          case "timestamp":
            return "global::System.DateTimeOffset";
          case "string":
            return "string";
          case "bytes":
            return "byte[]";
        }
      }
    }
  }

  getDefaultExpr(
    type: ResolvedType,
    fieldRecursivity?: false | "soft" | "via-optional" | "hard",
  ): string {
    switch (type.kind) {
      case "record": {
        const recordLocation = this.recordMap.get(type.key)!;
        const csharpType = this.getCsharpType(type);
        if (fieldRecursivity === "hard") {
          return "null";
        }
        if (recordLocation.record.recordType === "struct") {
          return `${csharpType}.DEFAULT`;
        }
        return `${csharpType}.DEFAULT`;
      }
      case "array": {
        const itemType = this.getCsharpType(type.item);
        return `global::System.Array.Empty<${itemType}>()`;
      }
      case "optional": {
        return "null";
      }
      case "primitive": {
        const { primitive } = type;
        switch (primitive) {
          case "bool":
            return "false";
          case "int32":
            return "0";
          case "int64":
            return "0L";
          case "hash64":
            return "0UL";
          case "float32":
            return "0.0f";
          case "float64":
            return "0.0";
          case "timestamp":
            return "default(global::System.DateTimeOffset)";
          case "string":
            return '""';
          case "bytes":
            return "global::System.Array.Empty<byte>()";
        }
      }
    }
  }
}
