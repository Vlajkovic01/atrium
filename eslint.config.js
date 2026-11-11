import js from "@eslint/js";
import tseslint from "typescript-eslint";

export default tseslint.config(
  {
    ignores: [
      "**/node_modules/**",
      "**/dist/**",
      "server/**",
      // Generated from the OpenAPI contract; never edited by hand.
      "packages/shared/src/api/schema.d.ts",
    ],
  },
  js.configs.recommended,
  ...tseslint.configs.strict,
  {
    rules: {
      // The non-null assertion is used where an index was just checked.
      "@typescript-eslint/no-non-null-assertion": "off",
    },
  },
);
