import { ApiError, errorMessageKeys } from "@atrium/shared";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
  type TextInputProps,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { radius, space, type, useTheme } from "../theme.ts";

/** A screen: paper background, safe areas, scrolls when the content or the keyboard needs it. */
export function Screen({ children, centered = false }: { readonly children: ReactNode; readonly centered?: boolean }) {
  const { colors } = useTheme();
  return (
    <SafeAreaView style={[styles.flex, { backgroundColor: colors.paper }]}>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView
          contentContainerStyle={[styles.screen, centered && styles.centered]}
          keyboardShouldPersistTaps="handled"
        >
          <View style={styles.column}>{children}</View>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

export function Title({ children }: { readonly children: ReactNode }) {
  const { colors } = useTheme();
  return (
    <Text accessibilityRole="header" style={[styles.title, { color: colors.ink }]}>
      {children}
    </Text>
  );
}

export function Heading({ children }: { readonly children: ReactNode }) {
  const { colors } = useTheme();
  return (
    <Text accessibilityRole="header" style={[styles.heading, { color: colors.ink }]}>
      {children}
    </Text>
  );
}

export function Body({ children, faint = false }: { readonly children: ReactNode; readonly faint?: boolean }) {
  const { colors } = useTheme();
  return <Text style={[styles.body, { color: faint ? colors.inkFaint : colors.inkSoft }]}>{children}</Text>;
}

type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";

export function Button({
  children,
  onPress,
  variant = "secondary",
  disabled = false,
  busy = false,
}: {
  readonly children: string;
  readonly onPress: () => void;
  readonly variant?: ButtonVariant;
  readonly disabled?: boolean;
  /** Work is under way: the button cannot be pressed and shows it. */
  readonly busy?: boolean;
}) {
  const { colors } = useTheme();
  const look = {
    primary: { background: colors.accent, ink: colors.accentInk, border: colors.accent },
    secondary: { background: colors.surface, ink: colors.ink, border: colors.lineStrong },
    ghost: { background: "transparent", ink: colors.accent, border: "transparent" },
    danger: { background: colors.surface, ink: colors.danger, border: colors.danger },
  }[variant];
  const inactive = disabled || busy;

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled: inactive, busy }}
      disabled={inactive}
      onPress={onPress}
      style={({ pressed }) => [
        styles.button,
        { backgroundColor: look.background, borderColor: look.border, opacity: inactive ? 0.55 : pressed ? 0.8 : 1 },
      ]}
    >
      {busy ? <ActivityIndicator color={look.ink} /> : null}
      <Text style={[styles.buttonText, { color: look.ink }]}>{children}</Text>
    </Pressable>
  );
}

export function TextField({ label, ...input }: TextInputProps & { readonly label: string }) {
  const { colors } = useTheme();
  return (
    <View style={styles.field}>
      <Text style={[styles.label, { color: colors.inkSoft }]}>{label}</Text>
      <TextInput
        accessibilityLabel={label}
        placeholderTextColor={colors.inkFaint}
        {...input}
        style={[styles.input, { backgroundColor: colors.surface, borderColor: colors.lineStrong, color: colors.ink }]}
      />
    </View>
  );
}

type NoticeKind = "info" | "warning" | "danger" | "accent";

/** A boxed message. Gold is for what needs care, red for what went wrong. */
export function Notice({ children, kind = "info" }: { readonly children: ReactNode; readonly kind?: NoticeKind }) {
  const { colors } = useTheme();
  const look = {
    info: { background: colors.infoWash, ink: colors.infoInk },
    warning: { background: colors.goldWash, ink: colors.goldInk },
    danger: { background: colors.dangerWash, ink: colors.dangerInk },
    accent: { background: colors.accentWash, ink: colors.accentWashInk },
  }[kind];
  return (
    <View
      accessibilityRole={kind === "danger" ? "alert" : undefined}
      style={[styles.notice, { backgroundColor: look.background }]}
    >
      <Text style={[styles.noticeText, { color: look.ink }]}>{children}</Text>
    </View>
  );
}

/** Says what went wrong, in the person's language: the server's error code translated, or the given message. */
export function ErrorMessage({ error }: { readonly error: unknown }) {
  const { t } = useTranslation();
  if (error === null || error === undefined) {
    return null;
  }

  if (typeof error === "string") {
    return <Notice kind="danger">{error}</Notice>;
  }

  const problem = error instanceof ApiError ? error.problem : { code: "unknown", params: {} };
  return <Notice kind="danger">{t([...errorMessageKeys(problem.code)], { ...problem.params })}</Notice>;
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  screen: { flexGrow: 1, padding: space.xl, alignItems: "center" },
  centered: { justifyContent: "center" },
  column: { width: "100%", maxWidth: 480, gap: space.lg },
  title: { fontSize: type.title, fontWeight: "700" },
  heading: { fontSize: type.heading, fontWeight: "600" },
  body: { fontSize: type.body, lineHeight: 24 },
  button: {
    minHeight: 52,
    borderRadius: radius.md,
    borderWidth: 1,
    paddingHorizontal: space.xl,
    paddingVertical: space.md,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: space.sm,
  },
  buttonText: { fontSize: type.body, fontWeight: "600", textAlign: "center" },
  field: { gap: space.xs },
  label: { fontSize: type.small, fontWeight: "600" },
  input: {
    minHeight: 52,
    borderRadius: radius.md,
    borderWidth: 1,
    paddingHorizontal: space.lg,
    fontSize: type.body,
  },
  notice: { borderRadius: radius.md, padding: space.lg },
  noticeText: { fontSize: type.small, lineHeight: 21 },
});
