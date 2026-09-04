fn compute(value: &mut [i32; 2], flag: bool) -> i32 {
    let mut result: i32 = value[0] + 2 * 3;
    if flag { result } else { return 0; }
}
