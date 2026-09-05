fn inc(mut x: i32) -> i32 { x = x + 1; x }
fn main() {
    println!("{}", inc(0b10_i32) + 0o10 + 0x10 + 1_0);
    println!("{}", -2147483648);
    println!("{}", !0i32);
    println!("{}", -1 <= 0 && 5 >= 5 && 2 != 3);
}
