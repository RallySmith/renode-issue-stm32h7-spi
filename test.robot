*** Settings ***
Suite Setup                   Setup
Suite Teardown                Teardown
Test Setup                    Reset Emulation
Test Teardown                 Test Teardown
Resource                      ${RENODEKEYWORDS}

*** Variables ***
${SCRIPT}                     ${CURDIR}/test.resc
${UART}                       sysbus.usart6


*** Keywords ***
Load Script
    Execute Script            ${SCRIPT}
    Create Terminal Tester    ${UART}
    Create Log Tester         1

*** Test Cases ***
Should Run Test Case
    [Timeout]                 20 seconds
    Load Script
    Start Emulation
    Should Not Be In Log      sysbus: [cpu: 0x8000FDC] ReadDoubleWord from non existing peripheral at 0x5802480C.
    Wait For Line On Uart     INFO:<ADAU1452: CoreStatus: 0x0000>
    Wait For Line On Uart     PASS:<Platform test done>
    Wait For Line On Uart     EXIT:<done>
