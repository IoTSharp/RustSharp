fn seed() -> i32 {
    7
}

fn main() {
    let values = [seed(); 2];
    println!("{}", values[0]);
    println!("{}", values[1]);
}
