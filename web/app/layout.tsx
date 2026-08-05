import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "EDR 能力验证控制台",
  description: "用于编排本地能力测试、管理运行制品并离线比较 EDR 导出日志。",
  openGraph: {
    title: "EDR 能力验证平台",
    description: "本地优先 · 离线比较 · 证据可追溯",
    locale: "zh_CN",
    type: "website",
    images: [
      {
        url: "/og.png",
        width: 1731,
        height: 909,
        alt: "EDR 能力验证平台",
      },
    ],
  },
  twitter: {
    card: "summary_large_image",
    title: "EDR 能力验证平台",
    description: "本地优先 · 离线比较 · 证据可追溯",
    images: ["/og.png"],
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="zh-CN">
      <body>{children}</body>
    </html>
  );
}
