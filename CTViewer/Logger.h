// Logger.h
#pragma once

// Define severity levels as simple constants
#define LOG_INFO 0
#define LOG_WARNING 1
#define LOG_ERROR 2

// The log function declaration that VolumeRenderer will use
void Log(const char* message, int severity = LOG_INFO);