# Contributing to Meshtastic MQTT Broker

Thank you for your interest in contributing! This document provides guidelines for contributing to the project.

## Getting Started

1. **Fork the repository** on GitHub
2. **Clone your fork** locally:
   ```bash
   git clone https://github.com/yourusername/meshtastic-mqtt-broker.git
   cd meshtastic-mqtt-broker
   ```
3. **Create a feature branch**:
   ```bash
   git checkout -b feature/my-new-feature
   ```

## Development Setup

### Prerequisites
- .NET 9.0 SDK
- Git
- A text editor or IDE (VS Code, Visual Studio, Rider)

### Building the Project
```bash
dotnet restore
dotnet build
```

### Running Locally
```bash
dotnet run
```

### Running Tests
```bash
dotnet test
```

## Making Changes

### Code Style
- Follow C# coding conventions
- Use meaningful variable and method names
- Add XML documentation comments for public APIs
- Keep methods focused and concise

### Commit Messages
Use clear, descriptive commit messages:
```
Add location filtering service

- Implement BlockPositionPackets feature
- Add position precision reduction
- Update configuration schema
```

### Testing
- Add unit tests for new features
- Ensure existing tests still pass
- Test your changes with a real Meshtastic network if possible

## Submitting Changes

1. **Push your changes** to your fork:
   ```bash
   git push origin feature/my-new-feature
   ```

2. **Create a Pull Request** on GitHub:
   - Provide a clear description of the changes
   - Reference any related issues
   - Include screenshots or examples if applicable

3. **Wait for review**:
   - Respond to feedback
   - Make requested changes
   - Keep your PR updated with the main branch

## Feature Requests

Have an idea for a new feature? We'd love to hear it!

1. Check if the feature is already requested in [Issues](https://github.com/yourusername/meshtastic-mqtt-broker/issues)
2. If not, create a new issue with:
   - Clear description of the feature
   - Use case / motivation
   - Example configuration or API

## Bug Reports

Found a bug? Please report it!

1. Check if it's already reported in [Issues](https://github.com/yourusername/meshtastic-mqtt-broker/issues)
2. If not, create a new issue with:
   - Steps to reproduce
   - Expected behavior
   - Actual behavior
   - Configuration (sanitized)
   - Logs (if relevant)

## Documentation

Documentation improvements are always welcome!

- Fix typos or clarify unclear sections
- Add examples
- Improve code comments
- Create tutorials or guides

## Code of Conduct

Be respectful and constructive:
- Be welcoming to newcomers
- Respect differing viewpoints
- Focus on what's best for the project
- Show empathy towards others

## Questions?

If you have questions about contributing:
- Open a [Discussion](https://github.com/yourusername/meshtastic-mqtt-broker/discussions)
- Ask in the Meshtastic Discord/Forum
- Create an issue with the "question" label

## License

By contributing, you agree that your contributions will be licensed under the GPL-3.0 License.

---

Thank you for making Meshtastic MQTT Broker better! 🎉
